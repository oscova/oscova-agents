using Oscova.Agents.Conversations;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Localization;

namespace Oscova.Agents.Infrastructure.Conversations;

/// <summary>Renders questions and parses replies through the shared plugin transport.</summary>
public sealed class TextChannelElicitationService : ITextChannelElicitationService
{
    /// <summary>Hard timeout for an entire elicitation, including all re-prompts.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Maximum number of reply attempts before auto-cancelling.</summary>
    public const int MaxAttempts = 3;

    private readonly PluginTextElicitationTransport _transport;
    private readonly TextChannelElicitationStrings _strings;
    private readonly ILogger<TextChannelElicitationService> _logger;

    public TextChannelElicitationService(
        PluginTextElicitationTransport transport,
        IElicitationTextLocalizer localizer,
        ILogger<TextChannelElicitationService> logger)
    {
        _transport = transport;
        _strings = new TextChannelElicitationStrings(localizer);
        _logger = logger;
    }

    public async Task<ElicitResult> HandleMcpElicitationAsync(
        ElicitRequestParams requestParams,
        PluginElicitationOrigin origin,
        CancellationToken cancellationToken)
    {
        var request = ElicitationUtilities.BuildRequest(requestParams, _logger);
        if (request is null)
        {
            return Cancel();
        }

        // Linked CTS gives us a single token for the prompt and every re-prompt that
        // expires after DefaultTimeout regardless of how many parse attempts ran.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            var prompt = BuildPrompt(request);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var reply = await _transport.SendPromptAndAwaitReplyAsync(origin, prompt, timeoutCts.Token).ConfigureAwait(false);
                if (reply is null)
                {
                    return Cancel();
                }

                var trimmed = reply.Trim();

                if (IsCancelKeyword(trimmed))
                {
                    await _transport.SendAcknowledgementAsync(origin, _strings.Cancelled, timeoutCts.Token).ConfigureAwait(false);
                    return Cancel();
                }

                if (TryBuildContent(request, reply, out var content, out var parseError))
                {
                    await _transport.SendAcknowledgementAsync(origin, _strings.Accepted, timeoutCts.Token).ConfigureAwait(false);
                    return new ElicitResult { Action = "accept", Content = content };
                }

                if (attempt < MaxAttempts)
                {
                    prompt = $"{parseError} {_strings.CancelInstruction}";
                }
            }

            await _transport.SendAcknowledgementAsync(
                origin,
                _strings.CancelledTooManyAttempts,
                timeoutCts.Token).ConfigureAwait(false);

            return Cancel();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Text elicitation on channel {Channel} timed out after {Timeout}.", origin.Key.ChannelId, DefaultTimeout);
            return Cancel();
        }
        catch (OperationCanceledException)
        {
            // Incoming caller token tripped — most likely the agent run / MCP tool call ended.
            return Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Text elicitation on channel {Channel} failed.", origin.Key.ChannelId);
            return Cancel();
        }
    }

    private static ElicitResult Cancel() => new() { Action = "cancel" };

    private bool IsCancelKeyword(string trimmedReply)
    {
        foreach (var keyword in _strings.CancelKeywords)
        {
            if (string.Equals(trimmedReply, keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Builds the user-facing prompt. Multi-field requests render as a numbered list.
    private string BuildPrompt(ElicitationRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(request.Message);

        switch (request.SchemaType)
        {
            case ElicitationSchemaType.Boolean:
                // The shared answer applies to every displayed condition.
                foreach (var field in request.BooleanFields) { AppendField(sb, field.Label, field.Description); }

                sb.AppendLine(_strings.BooleanHint);
                break;

            case ElicitationSchemaType.String:
                if (request.StringFields.Count == 1)
                {
                    var field = request.StringFields[0];
                    AppendField(sb, field.Label, field.Description);
                    sb.AppendLine(BuildStringHint(field));
                }
                else
                {
                    AppendNumberedFields(sb, request.StringFields.Select(f => (f.Label, f.Description)));
                    sb.AppendLine(_strings.MultiFieldInstruction);
                }

                break;

            case ElicitationSchemaType.Number:
                if (request.NumberFields.Count == 1)
                {
                    var field = request.NumberFields[0];
                    AppendField(sb, field.Label, field.Description);
                    sb.AppendLine(BuildNumberHint(field));
                }
                else
                {
                    AppendNumberedFields(sb, request.NumberFields.Select(f => (f.Label, f.Description)));
                    sb.AppendLine(_strings.MultiFieldInstruction);
                }

                break;

            case ElicitationSchemaType.Enum:
                {
                    foreach (var field in request.EnumFields)
                    {
                    AppendField(sb, field.Label, field.Description);
                    var optionList = string.Join(", ", field.Options.Select(o => o.Label));
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, _strings.EnumHintFormat, optionList));
                    }

                    if (request.EnumFields.Count > 1) { sb.AppendLine(_strings.MultiFieldInstruction); }

                    break;
                }
        }

        sb.Append(_strings.CancelInstruction);
        return sb.ToString();
    }

    private static void AppendField(StringBuilder sb, string label, string? description)
    {
        sb.AppendLine()
            .AppendLine(string.IsNullOrWhiteSpace(description) ? label : $"{label} — {description}");
    }

    private static void AppendNumberedFields(StringBuilder sb, IEnumerable<(string Label, string? Description)> fields)
    {
        sb.AppendLine();
        var index = 1;
        foreach (var (label, description) in fields)
        {
            sb.AppendLine(string.IsNullOrWhiteSpace(description)
                ? $"{index}. {label}"
                : $"{index}. {label} — {description}");
            index++;
        }
    }

    private string BuildStringHint(ElicitationStringField field)
    {
        if (field.MinLength.HasValue && field.MaxLength.HasValue)
        {
            return string.Format(CultureInfo.InvariantCulture, _strings.StringRangeHintFormat, field.MinLength.Value, field.MaxLength.Value);
        }

        if (field.MinLength.HasValue)
        {
            return string.Format(CultureInfo.InvariantCulture, _strings.StringMinLengthHintFormat, field.MinLength.Value);
        }

        if (field.MaxLength.HasValue)
        {
            return string.Format(CultureInfo.InvariantCulture, _strings.StringMaxLengthHintFormat, field.MaxLength.Value);
        }

        return _strings.StringHint;
    }

    private string BuildNumberHint(ElicitationNumberField field)
    {
        var baseHint = field.IsInteger ? _strings.IntegerHint : _strings.NumberHint;
        if (field.Minimum.HasValue && field.Maximum.HasValue)
        {
            return $"{baseHint} " + string.Format(CultureInfo.InvariantCulture, _strings.InvalidNumberRangeFormat, FormatNumber(field.Minimum.Value, field.IsInteger), FormatNumber(field.Maximum.Value, field.IsInteger));
        }

        return baseHint;
    }

    private static string FormatNumber(double value, bool isInteger)
        => isInteger
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    // Parses a reply against the schema. Success → typed JSON content; failure → short error for re-prompt.
    private bool TryBuildContent(
        ElicitationRequest request,
        string reply,
        out IDictionary<string, JsonElement> content,
        out string parseError)
    {
        content = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        parseError = string.Empty;
        var trimmedReply = reply.Trim();

        switch (request.SchemaType)
        {
            case ElicitationSchemaType.Boolean:
                if (TryParseBoolean(trimmedReply, out var booleanValue))
                {
                    content = ElicitationUtilities.BuildBooleanContent(request.BooleanFields, booleanValue);
                    return true;
                }

                parseError = _strings.InvalidBoolean;
                return false;

            case ElicitationSchemaType.String:
                return TryBuildStringContent(request, reply, out content, out parseError);

            case ElicitationSchemaType.Number:
                return TryBuildNumberContent(request, reply, out content, out parseError);

            case ElicitationSchemaType.Enum:
                return TryBuildEnumContent(request, reply, out content, out parseError);

            default:
                parseError = _strings.InvalidString;
                return false;
        }
    }

    private bool TryParseBoolean(string value, out bool result)
    {
        foreach (var keyword in _strings.BooleanTrueKeywords)
        {
            if (string.Equals(value, keyword, StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }
        }

        foreach (var keyword in _strings.BooleanFalseKeywords)
        {
            if (string.Equals(value, keyword, StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }
        }

        result = false;
        return false;
    }

    // Splits a multi-line reply into per-field values; line N pairs with field N.
    private bool TrySplitLines(string reply, int expectedCount, out string[] lines, out string parseError)
    {
        parseError = string.Empty;
        lines = reply.Split('\n', StringSplitOptions.None)
            .Select(l => l.Trim())
            .ToArray();

        if (lines.Length > expectedCount)
        {
            parseError = string.Format(CultureInfo.InvariantCulture, _strings.InvalidLineCountFormat, expectedCount, lines.Length);
            return false;
        }

        if (lines.Length < expectedCount)
        {
            lines = [.. lines, .. Enumerable.Repeat(string.Empty, expectedCount - lines.Length)];
        }

        return true;
    }

    private bool TryBuildStringContent(
        ElicitationRequest request,
        string reply,
        out IDictionary<string, JsonElement> content,
        out string parseError)
    {
        content = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        parseError = string.Empty;
        var fields = request.StringFields;

        var rawValues = new Dictionary<string, string>(fields.Count, StringComparer.OrdinalIgnoreCase);

        if (fields.Count == 1)
        {
            var value = reply.Trim();
            if (!ValidateStringValue(request, fields[0], value, out parseError))
            {
                return false;
            }

            if (value.Length > 0)
            {
                rawValues[fields[0].Name] = value;
            }
        }
        else
        {
            if (!TrySplitLines(reply, fields.Count, out var lines, out parseError))
            {
                return false;
            }

            for (var i = 0; i < fields.Count; i++)
            {
                if (!ValidateStringValue(request, fields[i], lines[i], out parseError))
                {
                    return false;
                }

                if (lines[i].Length > 0)
                {
                    rawValues[fields[i].Name] = lines[i];
                }
            }
        }

        content = ElicitationUtilities.BuildStringContent(fields, rawValues);
        return true;
    }

    private bool ValidateStringValue(ElicitationRequest request, ElicitationStringField field, string value, out string parseError)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (request.IsFieldRequired(field.Name))
            {
                parseError = _strings.InvalidString;
                return false;
            }

            parseError = string.Empty;
            return true;
        }

        if (field.MinLength.HasValue && value.Length < field.MinLength.Value)
        {
            parseError = string.Format(CultureInfo.InvariantCulture, _strings.InvalidStringTooShortFormat, field.MinLength.Value);
            return false;
        }

        if (field.MaxLength.HasValue && value.Length > field.MaxLength.Value)
        {
            parseError = string.Format(CultureInfo.InvariantCulture, _strings.InvalidStringTooLongFormat, field.MaxLength.Value);
            return false;
        }

        parseError = string.Empty;
        return true;
    }

    private bool TryBuildNumberContent(
        ElicitationRequest request,
        string reply,
        out IDictionary<string, JsonElement> content,
        out string parseError)
    {
        content = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        parseError = string.Empty;
        var fields = request.NumberFields;

        var rawValues = new Dictionary<string, string>(fields.Count, StringComparer.OrdinalIgnoreCase);

        if (fields.Count == 1)
        {
            var value = reply.Trim();
            if (!ValidateNumberValue(request, fields[0], value, out parseError))
            {
                return false;
            }

            if (value.Length > 0)
            {
                rawValues[fields[0].Name] = value;
            }
        }
        else
        {
            if (!TrySplitLines(reply, fields.Count, out var lines, out parseError))
            {
                return false;
            }

            for (var i = 0; i < fields.Count; i++)
            {
                if (!ValidateNumberValue(request, fields[i], lines[i], out parseError))
                {
                    return false;
                }

                if (lines[i].Length > 0)
                {
                    rawValues[fields[i].Name] = lines[i];
                }
            }
        }

        content = ElicitationUtilities.BuildNumberContent(fields, rawValues);
        return true;
    }

    private bool ValidateNumberValue(ElicitationRequest request, ElicitationNumberField field, string value, out string parseError)
    {
        // Parse strictly with InvariantCulture — matches ElicitationUtilities.BuildNumberContent
        // and the JSON wire format. A user typing "1,5" gets a parse error, by design.
        if (string.IsNullOrEmpty(value))
        {
            if (request.IsFieldRequired(field.Name))
            {
                parseError = field.IsInteger
                    ? _strings.InvalidInteger
                    : _strings.InvalidNumber;
                return false;
            }

            parseError = string.Empty;
            return true;
        }

        if (field.IsInteger)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                parseError = _strings.InvalidInteger;
                return false;
            }

            var outOfRange = (field.Minimum is { } min && intValue < new BigInteger(Math.Ceiling(min))) ||
                (field.Maximum is { } max && intValue > new BigInteger(Math.Floor(max)));
            return CheckNumberRange(field, outOfRange, out parseError);
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            parseError = _strings.InvalidNumber;
            return false;
        }

        return CheckNumberRange(field,
            (field.Minimum.HasValue && doubleValue < field.Minimum.Value) ||
            (field.Maximum.HasValue && doubleValue > field.Maximum.Value), out parseError);
    }

    private bool CheckNumberRange(ElicitationNumberField field, bool outOfRange, out string parseError)
    {
        if (outOfRange)
        {
            // Range message uses formatted Min/Max for symmetry with the prompt hint.
            parseError = string.Format(
                CultureInfo.InvariantCulture,
                _strings.InvalidNumberRangeFormat,
                field.Minimum.HasValue ? FormatNumber(field.Minimum.Value, field.IsInteger) : "?",
                field.Maximum.HasValue ? FormatNumber(field.Maximum.Value, field.IsInteger) : "?");
            return false;
        }

        parseError = string.Empty;
        return true;
    }

    private bool TryBuildEnumContent(
        ElicitationRequest request,
        string rawReply,
        out IDictionary<string, JsonElement> content,
        out string parseError)
    {
        content = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var fields = request.EnumFields;
        // Collect one line per field, using the same omission rules as text and numeric questions.
        if (!TrySplitLines(rawReply, fields.Count, out var lines, out parseError)) { return false; }

        var rawValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var reply = lines[i];
            if (string.IsNullOrEmpty(reply) && !request.IsFieldRequired(field.Name)) { continue; }

            var match = field.Options.FirstOrDefault(o =>
                string.Equals(o.Value, reply, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.Label, reply, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var optionList = string.Join(", ", field.Options.Select(o => o.Label));
                parseError = string.Format(CultureInfo.InvariantCulture, _strings.InvalidEnumFormat, optionList);
                return false;
            }

            rawValues[field.Name] = match.Value;
        }

        content = ElicitationUtilities.BuildEnumContent(fields, rawValues);
        parseError = string.Empty;
        return true;
    }
}
