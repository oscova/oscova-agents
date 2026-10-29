using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Harness;
using Oscova.Agents.Middleware;
using System.Collections;
using System.Text;
using System.Text.Json;

namespace Oscova.Agents.Core.Harness;

/// <summary>Turns tool failures into results the agent can read, and folds repeated failures into one.</summary>
public static class ToolFailureMiddleware
{
    private const string FailureMarker = ToolFailureException.ResultPrefix;
    private const int FoldThreshold = 3;

    /// <summary>Default recovery wording used when no user instruction file is available.</summary>
    public const string DefaultRecoveryInstructions = "Retrying with the same arguments will not work. Change the arguments or take a different approach.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Adds tool failure reporting and repeated-failure folding to the chat client.</summary>
    public static ChatClientBuilder UseToolFailureReporting(this ChatClientBuilder builder, string recoveryInstructions = DefaultRecoveryInstructions)
    {
        return FunctionInvocationComposition.Contribute(builder, fic =>
        {
            var previousInvoker = fic.FunctionInvoker;
            fic.FunctionInvoker = async (context, ct) =>
            {
                try
                {
                    ValidateEnumArguments(context);
                    return previousInvoker is null
                        ? await context.Function.InvokeAsync(context.Arguments, ct).ConfigureAwait(false)
                        : await previousInvoker(context, ct).ConfigureAwait(false);
                }
                catch (ToolFailureException failure)
                {
                    return HandleFailure(context, failure, recoveryInstructions);
                }
            };
        });
    }

    // Enum conversion fails before a tool can report its own input error.
    private static void ValidateEnumArguments(FunctionInvocationContext context)
    {
        var schema = context.Function.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties))
        {
            return;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty("enum", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || !context.Arguments.TryGetValue(property.Name, out var argument))
            {
                continue;
            }

            var text = argument switch
            {
                string value => value,
                JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
                _ => null
            };
            if (text is null || choices.EnumerateArray().Any(choice =>
                choice.ValueKind != JsonValueKind.String
                || string.Equals(choice.GetString(), text, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            throw new ToolFailureException(
                $"Invalid argument '{property.Name}'.",
                $"Allowed values: {string.Join(", ", choices.EnumerateArray().Select(static choice => choice.GetString()))}.");
        }
    }

    private static string HandleFailure(FunctionInvocationContext context, ToolFailureException failure, string recoveryInstructions)
    {
        var text = failure.Fix is null
            ? FailureMarker + failure.Message
            : $"{FailureMarker}{failure.Message} {failure.Fix}";

        // Per-call persistence owns prior tool rounds outside the function loop's transient buffer.
        var session = AIAgent.CurrentRunContext?.Session;
        IList<ChatMessage> messages = session is not null && session.TryGetInMemoryChatHistory(out var history)
            ? history
            : context.Messages;
        var prior = FindPriorFailures(context, messages);
        if (prior.Count + 1 < FoldThreshold)
        {
            return text;
        }

        // Approval replay holds insertion positions that must remain stable.
        if (!HasFunctionApproval(messages) && !HasFunctionApproval(context.Messages))
        {
            RemoveRoundTrips(messages, prior);
        }

        return ComposeFoldedFailure(context.Function.Name, prior, text, recoveryInstructions);
    }

    private static List<FunctionResultContent> FindPriorFailures(FunctionInvocationContext context, IEnumerable<ChatMessage> messages)
    {
        var signature = BuildSignature(context.Function.Name, context.Arguments);
        var currentCallId = context.CallContent?.CallId;
        var matchingCallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call
                    && !string.Equals(call.CallId, currentCallId, StringComparison.Ordinal)
                    && string.Equals(BuildSignature(call.Name, call.Arguments), signature, StringComparison.Ordinal))
                {
                    matchingCallIds.Add(call.CallId);
                }
            }
        }

        var failures = new List<FunctionResultContent>();
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent result
                    && matchingCallIds.Contains(result.CallId)
                    && AsResultText(result.Result) is { } resultText
                    && resultText.StartsWith(FailureMarker, StringComparison.Ordinal))
                {
                    failures.Add(result);
                }
            }
        }

        return failures;
    }

    private static string? AsResultText(object? result) => result switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null
    };

    private static string ComposeFoldedFailure(
        string toolName,
        IReadOnlyList<FunctionResultContent> prior,
        string currentText,
        string recoveryInstructions)
    {
        var reasons = new List<string>(prior.Count + 1);
        foreach (var result in prior)
        {
            reasons.Add(StripMarker(AsResultText(result.Result)));
        }

        reasons.Add(StripMarker(currentText));

        var builder = new StringBuilder(FailureMarker);
        if (AllShareOneReason(reasons))
        {
            builder.Append($"'{toolName}' failed {reasons.Count} times with these arguments, each time: {reasons[0]}")
                .AppendLine();
        }
        else
        {
            builder.AppendLine($"'{toolName}' failed {reasons.Count} times with these arguments:");
            for (var i = 0; i < reasons.Count; i++)
            {
                builder.Append(i + 1).Append(". ").AppendLine(reasons[i]);
            }
        }

        builder.AppendLine()
            .Append(recoveryInstructions);
        return builder.ToString();
    }

    private static void RemoveRoundTrips(IList<ChatMessage> messages, IReadOnlyList<FunctionResultContent> failures)
    {
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var failure in failures)
        {
            callIds.Add(failure.CallId);
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            var retained = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                var isTarget = content switch
                {
                    FunctionCallContent call => callIds.Contains(call.CallId),
                    FunctionResultContent result => callIds.Contains(result.CallId),
                    _ => false
                };

                if (!isTarget)
                {
                    retained.Add(content);
                }
            }

            // Nothing pruned here: leave the original instance in place.
            if (retained.Count == message.Contents.Count)
            {
                continue;
            }

            if (retained.Count == 0)
            {
                messages.RemoveAt(i);
                continue;
            }

            // Clone shares Contents, so the new list has to be assigned onto the clone.
            var pruned = message.Clone();
            pruned.Contents = retained;
            messages[i] = pruned;
        }
    }

    private static bool HasFunctionApproval(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is ToolApprovalRequestContent { ToolCall: FunctionCallContent { InformationalOnly: false } }
                    or ToolApprovalResponseContent { ToolCall: FunctionCallContent { InformationalOnly: false } })
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string BuildSignature(string name, IDictionary<string, object?>? arguments) =>
        name + "|" + CanonicalizeArguments(arguments);

    private static string CanonicalizeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        var normalized = NormalizeDictionary(arguments);
        try
        {
            return JsonSerializer.Serialize(normalized, JsonOptions);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return string.Join("|", normalized.Select(static pair => $"{pair.Key}={pair.Value}"));
        }
    }

    private static SortedDictionary<string, object?> NormalizeDictionary(IEnumerable<KeyValuePair<string, object?>> values)
    {
        var normalized = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            normalized[key] = NormalizeValue(value);
        }

        return normalized;
    }

    private static object? NormalizeValue(object? value)
    {
        return value switch
        {
            null => null,
            JsonElement jsonElement => NormalizeJsonElement(jsonElement),
            string => value,
            IDictionary<string, object?> dictionary => NormalizeDictionary(dictionary),
            IEnumerable<KeyValuePair<string, object?>> pairs => NormalizeDictionary(pairs),
            IEnumerable enumerable => NormalizeEnumerable(enumerable),
            _ => value
        };
    }

    private static object? NormalizeJsonElement(JsonElement jsonElement)
    {
        return jsonElement.ValueKind switch
        {
            JsonValueKind.Object => NormalizeJsonObject(jsonElement),
            JsonValueKind.Array => jsonElement.EnumerateArray().Select(NormalizeJsonElement).ToList(),
            JsonValueKind.String => jsonElement.GetString(),
            // Raw text avoids range-limited numeric conversions while preserving exact duplicate identity.
            JsonValueKind.Number => jsonElement.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => jsonElement.GetRawText()
        };
    }

    private static SortedDictionary<string, object?> NormalizeJsonObject(JsonElement jsonElement)
    {
        var normalized = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in jsonElement.EnumerateObject())
        {
            normalized[property.Name] = NormalizeJsonElement(property.Value);
        }

        return normalized;
    }

    private static List<object?> NormalizeEnumerable(IEnumerable values)
    {
        var normalized = new List<object?>();
        foreach (var value in values)
        {
            normalized.Add(NormalizeValue(value));
        }

        return normalized;
    }

    private static string StripMarker(string? text)
    {
        if (text?.StartsWith(FailureMarker, StringComparison.Ordinal) == true)
        {
            return text[FailureMarker.Length..].Trim();
        }

        return text?.Trim() ?? string.Empty;
    }

    private static bool AllShareOneReason(IReadOnlyList<string> reasons)
    {
        for (var i = 1; i < reasons.Count; i++)
        {
            if (!string.Equals(reasons[0], reasons[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
