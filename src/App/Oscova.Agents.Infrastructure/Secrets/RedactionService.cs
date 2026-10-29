using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Secrets;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Secrets;

/// <summary>
/// Applies structured key-name and user credential value redaction before content leaves trusted runtime state.
/// </summary>
public sealed class RedactionService(
    ICredentialService credentialService,
    ICredentialTypeRegistry credentialTypeRegistry,
    Func<string, ICredentialService>? credentialServiceFactory = null) : IRedactionService
{
    private const int MinimumKnownSecretLength = 8;
    private const string ValueMask = "***";

    public async Task<string> RedactTextAsync(string text, string? userId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var knownSecrets = await ResolveKnownSecretsAsync(userId, ct).ConfigureAwait(false);
        return RedactText(text, knownSecrets);
    }

    public async Task<IDictionary<string, object?>> RedactArgumentsAsync(
        IDictionary<string, object?> arguments,
        string? userId = null,
        CancellationToken ct = default)
    {
        var knownSecrets = await ResolveKnownSecretsAsync(userId, ct).ConfigureAwait(false);
        return RedactArguments(arguments, knownSecrets);
    }

    public async Task<IReadOnlyList<ChatMessage>> RedactMessagesAsync(
        IReadOnlyList<ChatMessage> messages,
        string? userId = null,
        CancellationToken ct = default)
    {
        var knownSecrets = await ResolveKnownSecretsAsync(userId, ct).ConfigureAwait(false);
        var redacted = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            redacted.Add(RedactMessage(message, knownSecrets));
        }

        return redacted;
    }

    private async Task<IReadOnlySet<string>> ResolveKnownSecretsAsync(string? userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var userCredentialService = credentialServiceFactory?.Invoke(userId) ?? credentialService;
        var rows = await userCredentialService.GetAllRowsAsync(ct).ConfigureAwait(false);
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var fieldName in GetSecretFieldNames(row))
            {
                var value = await userCredentialService.ResolveFieldAsync(row.CredentialKey, fieldName, ct).ConfigureAwait(false);
                // Short values are intentionally excluded because replacing common text would corrupt transcripts.
                if (!string.IsNullOrEmpty(value) && value.Length >= MinimumKnownSecretLength)
                {
                    secrets.Add(value);
                    // Managed OAuth payloads contain independently usable secrets inside their JSON value.
                    if (row.IsManaged && PluginCredentialKeys.IsOAuthToken(row.CredentialKey) &&
                        JsonSerializer.Deserialize<OAuthTokenData>(value, JsonSerializerOptions.Web) is { } token)
                    {
                        if (token.AccessToken.Length >= MinimumKnownSecretLength) { secrets.Add(token.AccessToken); }

                        if (token.RefreshToken.Length >= MinimumKnownSecretLength) { secrets.Add(token.RefreshToken); }
                    }
                }
            }
        }

        return secrets;
    }

    private IEnumerable<string> GetSecretFieldNames(UserCredential row)
    {
        var declaration = credentialTypeRegistry.Find(row.CredentialKey);
        if (declaration is not null)
        {
            return declaration.Fields
                .Where(static field => field.IsSecret)
                .Select(static field => field.Name);
        }

        return ReadStoredFieldNames(row.Data);
    }

    private static IEnumerable<string> ReadStoredFieldNames(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return [];
        }

        using var document = JsonDocument.Parse(data);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? [.. document.RootElement.EnumerateObject().Select(static property => property.Name)]
            : [];
    }

    private static string RedactText(string text, IReadOnlySet<string> knownSecrets)
    {
        foreach (var secret in knownSecrets.OrderByDescending(static secret => secret.Length))
        {
            text = text.Replace(secret, ValueMask, StringComparison.Ordinal);
        }

        return text;
    }

    private static IDictionary<string, object?> RedactArguments(
        IDictionary<string, object?> arguments,
        IReadOnlySet<string> knownSecrets)
    {
        var keyRedacted = ToolCallArgumentRedactor.Redact(arguments);
        return RedactArgumentValues(keyRedacted, knownSecrets);
    }

    private static Dictionary<string, object?> RedactArgumentValues(
        IDictionary<string, object?> arguments,
        IReadOnlySet<string> knownSecrets)
    {
        var redacted = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var pair in arguments)
        {
            redacted[pair.Key] = RedactValue(pair.Value, knownSecrets);
        }

        return redacted;
    }

    private static object? RedactValue(object? value, IReadOnlySet<string> knownSecrets)
    {
        return value switch
        {
            null => null,
            string text => RedactText(text, knownSecrets),
            JsonElement element => RedactJsonElement(element, knownSecrets),
            IDictionary<string, object?> dictionary => RedactArgumentValues(dictionary, knownSecrets),
            IDictionary dictionary => RedactDictionary(dictionary, knownSecrets),
            IEnumerable enumerable when value is not string => RedactEnumerable(enumerable, knownSecrets),
            _ when IsScalarValue(value) => value,
            _ => RedactJsonElement(JsonSerializer.SerializeToElement(value), knownSecrets)
        };
    }

    private static bool IsScalarValue(object value)
    {
        return value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
            or DateTime or DateTimeOffset or Guid or Uri;
    }

    private static Dictionary<string, object?> RedactDictionary(IDictionary dictionary, IReadOnlySet<string> knownSecrets)
    {
        var redacted = new Dictionary<string, object?>(dictionary.Count, StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            redacted[key] = RedactValue(entry.Value, knownSecrets);
        }

        return redacted;
    }

    private static List<object?> RedactEnumerable(IEnumerable enumerable, IReadOnlySet<string> knownSecrets)
    {
        var redacted = new List<object?>();
        foreach (var item in enumerable)
        {
            redacted.Add(RedactValue(item, knownSecrets));
        }

        return redacted;
    }

    private static object? RedactJsonElement(JsonElement element, IReadOnlySet<string> knownSecrets)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    static property => property.Name,
                    property => RedactJsonElement(property.Value, knownSecrets),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => RedactJsonElement(item, knownSecrets))
                .ToList(),
            JsonValueKind.String => RedactText(element.GetString() ?? string.Empty, knownSecrets),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.TryGetDouble(out var doubleValue)
                    ? doubleValue
                    : element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => RedactText(element.GetRawText(), knownSecrets)
        };
    }

    private static ChatMessage RedactMessage(ChatMessage message, IReadOnlySet<string> knownSecrets)
    {
        var clone = message.Clone();
        clone.Contents = [.. message.Contents.Select(content => RedactContent(content, knownSecrets))];
        return clone;
    }

    private static AIContent RedactContent(AIContent content, IReadOnlySet<string> knownSecrets)
    {
        return content switch
        {
            TextContent text => CopyMetadata(text, new TextContent(RedactText(text.Text, knownSecrets))),
            FunctionCallContent call => CopyMetadata(call, new FunctionCallContent(
                    call.CallId,
                    call.Name,
                    RedactArguments(call.Arguments ?? new Dictionary<string, object?>(), knownSecrets))
                {
                    Exception = call.Exception,
                    InformationalOnly = call.InformationalOnly
                }),
            FunctionResultContent result => CopyMetadata(result, new FunctionResultContent(
                    result.CallId,
                    RedactValue(result.Result, knownSecrets))
                {
                    Exception = result.Exception
                }),
            _ => content
        };
    }

    private static TContent CopyMetadata<TContent>(AIContent source, TContent target)
        where TContent : AIContent
    {
        target.Annotations = source.Annotations;
        target.RawRepresentation = source.RawRepresentation;
        target.AdditionalProperties = source.AdditionalProperties;
        return target;
    }

}
