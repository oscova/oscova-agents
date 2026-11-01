using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Oscova.Agents.Middleware;

/// <summary>
/// Sends the model provider an ASCII alias for every tool name it would reject and maps the provider's calls back.
/// </summary>
/// <remarks>
/// OpenAI-compatible and Gemini providers accept only function names that start with a letter or underscore and use
/// <c>[A-Za-z0-9_-]</c>, up to 64 characters. Tool names keep their real form everywhere else (settings, approval
/// policies, governance, history), so an MCP server named <c>Café</c> works without changing any stored name.
/// </remarks>
public sealed class ProviderToolNameChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private const int HashLength = 8;

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var aliases = new AliasMap();
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        var providerMessages = aliases.Rewrite(history);
        var providerOptions = aliases.Rewrite(options);
        aliases.ThrowIfAmbiguous(history, options);

        var response = await base.GetResponseAsync(providerMessages, providerOptions, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
        {
            aliases.Restore(message.Contents);
        }

        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var aliases = new AliasMap();
        var history = messages as IList<ChatMessage> ?? messages.ToList();
        var providerMessages = aliases.Rewrite(history);
        var providerOptions = aliases.Rewrite(options);
        aliases.ThrowIfAmbiguous(history, options);

        await foreach (var update in base.GetStreamingResponseAsync(providerMessages, providerOptions, cancellationToken).ConfigureAwait(false))
        {
            aliases.Restore(update.Contents);
            yield return update;
        }
    }

    /// <summary>Returns true when every provider accepts the name as a function name.</summary>
    internal static bool IsProviderSafe(string name) =>
        name.Length is > 0 and <= ToolNaming.MaxWireNameLength &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    /// <summary>Builds the provider alias for an unsafe name: its ASCII fold plus a hash of the real name.</summary>
    /// <remarks>
    /// The alias depends on the real name only, so a call replayed from history keeps the alias the provider saw,
    /// and two names with the same fold (<c>café</c>, <c>cafè</c>) still get different aliases.
    /// </remarks>
    internal static string CreateAlias(string name)
    {
        var builder = new StringBuilder(name.Length + 1);
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
            {
                builder.Append(ch);
            }
            else if (builder.Length == 0 || builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        if (builder.Length == 0 || !(char.IsAsciiLetter(builder[0]) || builder[0] == '_'))
        {
            builder.Insert(0, '_');
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..HashLength].ToLowerInvariant();
        var stemLength = Math.Min(builder.Length, ToolNaming.MaxWireNameLength - HashLength - 1);
        return builder.ToString(0, stemLength) + "_" + hash;
    }

    private static FunctionCallContent Rename(FunctionCallContent call, string name, object? rawRepresentation) => new(call.CallId, name, call.Arguments)
    {
        Exception = call.Exception,
        InformationalOnly = call.InformationalOnly,
        Annotations = call.Annotations,
        RawRepresentation = rawRepresentation,
        AdditionalProperties = call.AdditionalProperties
    };

    /// <summary>Holds one request's real-to-alias names, so the response maps back only what this request sent.</summary>
    private sealed class AliasMap
    {
        private readonly Dictionary<string, string> _realByAlias = new(StringComparer.Ordinal);

        public IEnumerable<ChatMessage> Rewrite(IList<ChatMessage> messages)
        {
            if (!messages.Any(message => message.Contents.Any(IsUnsafeCall)))
            {
                return messages;
            }

            // The caller's history is shared, so changed messages are copied, never edited.
            return messages.Select(message =>
            {
                if (!message.Contents.Any(IsUnsafeCall))
                {
                    return message;
                }

                var copy = message.Clone();
                // A provider adapter can resend its raw part verbatim, so a renamed call drops it and the provider sees the alias.
                copy.Contents = [.. message.Contents.Select(content =>
                    content is FunctionCallContent call && !IsProviderSafe(call.Name) ? Rename(call, GetAlias(call.Name), rawRepresentation: null) : content)];
                return copy;
            }).ToList();
        }

        public ChatOptions? Rewrite(ChatOptions? options)
        {
            var requiredName = (options?.ToolMode as RequiredChatToolMode)?.RequiredFunctionName;
            var hasUnsafeTool = options?.Tools?.Any(tool => tool is AIFunctionDeclaration declaration && !IsProviderSafe(declaration.Name)) == true;
            if (options is null || (!hasUnsafeTool && (requiredName is null || IsProviderSafe(requiredName))))
            {
                return options;
            }

            var copy = options.Clone();
            copy.Tools = options.Tools?.Select(tool => tool is AIFunctionDeclaration declaration && !IsProviderSafe(declaration.Name)
                ? new AliasedDeclaration(declaration, GetAlias(declaration.Name))
                : tool).ToList();
            if (requiredName is not null && !IsProviderSafe(requiredName))
            {
                copy.ToolMode = ChatToolMode.RequireSpecific(GetAlias(requiredName));
            }

            return copy;
        }

        /// <summary>Fails when a provider-safe name in the request equals an alias, because the provider's call to it could not be routed.</summary>
        public void ThrowIfAmbiguous(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            if (_realByAlias.Count == 0)
            {
                return;
            }

            var names = (options?.Tools?.OfType<AIFunctionDeclaration>().Select(declaration => declaration.Name) ?? [])
                .Concat(messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.Name));
            if (names.FirstOrDefault(_realByAlias.ContainsKey) is { } name)
            {
                throw new InvalidOperationException(
                    $"Tool '{name}' has the same provider name as tool '{_realByAlias[name]}'. Rename one of them or its MCP server.");
            }
        }

        public void Restore(IList<AIContent> contents)
        {
            if (_realByAlias.Count == 0)
            {
                return;
            }

            for (var index = 0; index < contents.Count; index++)
            {
                if (contents[index] is FunctionCallContent call && _realByAlias.TryGetValue(call.Name, out var realName))
                {
                    contents[index] = Rename(call, realName, call.RawRepresentation);
                }
            }
        }

        private string GetAlias(string name)
        {
            var alias = CreateAlias(name);
            if (_realByAlias.TryGetValue(alias, out var existing) && existing != name)
            {
                throw new InvalidOperationException(
                    $"Tools '{existing}' and '{name}' have the same provider name. Rename one of them or its MCP server.");
            }

            _realByAlias[alias] = name;
            return alias;
        }

        private static bool IsUnsafeCall(AIContent content) => content is FunctionCallContent call && !IsProviderSafe(call.Name);
    }

    /// <summary>Presents a tool declaration to the provider under its alias.</summary>
    private sealed class AliasedDeclaration(AIFunctionDeclaration inner, string alias) : AIFunctionDeclaration
    {
        public override string Name => alias;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            base.GetService(serviceType, serviceKey) ?? inner.GetService(serviceType, serviceKey);
    }
}
