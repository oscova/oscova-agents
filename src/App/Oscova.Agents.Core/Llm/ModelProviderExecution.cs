using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Core.Llm;

/// <summary>Prevents retained model clients from using a superseded provider configuration.</summary>
public static class ModelProviderExecution
{
    /// <summary>Identifies a provider shared by all accounts in this installation.</summary>
    public static string Key(Guid id) => $"model-provider/{id:N}";

    /// <summary>Compares execution settings while allowing labels and accounting rates to change independently.</summary>
    public static bool Equivalent(ModelProviderConfig left, ModelProviderConfig? right)
    {
        if (right is null) { return false; }

        var comparable = right.Clone();
        comparable.Name = left.Name;
        comparable.InputCostPerMillionTokens = left.InputCostPerMillionTokens;
        comparable.OutputCostPerMillionTokens = left.OutputCostPerMillionTokens;
        return left.EqualsConfiguration(comparable);
    }

    /// <summary>Admits a model call and retains its dependency for the complete agent run.</summary>
    public static ExecutionPermissionRegistry.Registration Admit(ModelProviderConfig snapshot, long version,
        ModelProviderSettings settings, ExecutionPermissionRegistry executions, RunContextAccessor runs, bool embedding, CancellationToken ct)
    {
        var key = Key(snapshot.Id);
        var registration = executions.RegisterConfiguration(key, version, settings.UserId, ct);
        try
        {
            runs.Current?.ConfigurationScope?.Track(executions, key, version);
            if (settings.EmbeddingProviderIds.Contains(snapshot.Id) != embedding || !Equivalent(snapshot, settings.ModelProviderConfigs.FirstOrDefault(provider => provider.Id == snapshot.Id)))
            {
                throw new ModelProviderUnavailableException("The selected provider changed or is unavailable. Check model provider settings and start a new run.");
            }

            registration.Token.ThrowIfCancellationRequested();
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    /// <summary>Applies the same current-configuration check to chat and streaming requests.</summary>
    public sealed class ChatClient(IChatClient inner, ModelProviderConfig snapshot, long version,
        ModelProviderSettings settings, ExecutionPermissionRegistry executions, RunContextAccessor runs,
        ModelProviderActivity activity) : DelegatingChatClient(inner)
    {
        /// <summary>Routing wrappers borrow the raw client owned by the user-scoped cache.</summary>
        protected override void Dispose(bool disposing) { }

        /// <inheritdoc />
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await settings.RequireAccessAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
            using var call = Admit(snapshot, version, settings, executions, runs, false, cancellationToken);
            // Request activity ends here even when the run retains configuration permission.
            using var request = activity.Begin(runs.Current?.UserId ?? settings.UserId, snapshot.Id,
                runs.Current?.ExecutionKind ?? RunExecutionKind.WebChat);
            return await base.GetResponseAsync(messages, options, call.Token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await settings.RequireAccessAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
            using var call = Admit(snapshot, version, settings, executions, runs, false, cancellationToken);
            using var request = activity.Begin(runs.Current?.UserId ?? settings.UserId, snapshot.Id,
                runs.Current?.ExecutionKind ?? RunExecutionKind.WebChat);
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, call.Token).ConfigureAwait(false))
            {
                yield return update;
            }
        }
    }

    /// <summary>Applies provider edits to background embedding calls as well as conversational requests.</summary>
    public sealed class EmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, ModelProviderConfig snapshot, long version,
        ModelProviderSettings settings, ExecutionPermissionRegistry executions, RunContextAccessor runs,
        ModelProviderActivity activity)
        : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
    {
        /// <inheritdoc />
        public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            await settings.RequireAccessAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
            using var call = Admit(snapshot, version, settings, executions, runs, true, cancellationToken);
            using var request = activity.Begin(runs.Current?.UserId ?? settings.UserId, snapshot.Id,
                runs.Current?.ExecutionKind ?? RunExecutionKind.TaskRun);
            return await base.GenerateAsync(values, options, call.Token).ConfigureAwait(false);
        }
    }
}
