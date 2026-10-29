using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Notifications;
using Oscova.Agents.Policies;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Oscova.Agents.Core.Llm;

/// <summary>
/// Wraps the default model provider's chat client and retries a configured fallback before any response content is emitted.
/// </summary>
public sealed class FallbackChatClient(
    IChatClient primary,
    Func<CancellationToken, ValueTask<IChatClient>>? fallbackFactory,
    string defaultProviderId,
    EgressClass? fallbackEgressClass,
    string? fallbackProviderId,
    string? fallbackModelId,
    RunContextAccessor runContextAccessor,
    string userId,
    ILogger logger,
    NotificationHub<NotificationMessage>? notificationMessages = null) : DelegatingChatClient(primary)
{
    private IChatClient? _fallbackClient;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (fallbackFactory is null)
            {
                await LogDefaultFailedNoFallbackAsync(ex).ConfigureAwait(false);
                throw;
            }

            try
            {
                var fallback = await GetFallbackAsync(cancellationToken).ConfigureAwait(false);
                // A dispatched request remains egress even if the provider later fails.
                RecordFallbackEgress();
                var response = await fallback.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                await LogServedViaFallbackAsync(ex).ConfigureAwait(false);
                MarkFallbackActivated();
                return response;
            }
            catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
            {
                await LogBothFailedAsync(ex, fallbackEx).ConfigureAwait(false);
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var yieldedContent = false;
        Exception? preChunkFailure = null;

        var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (Exception ex) when (!yieldedContent && ex is not OperationCanceledException)
                {
                    preChunkFailure = ex;
                    break;
                }

                yieldedContent |= update.Contents.Count > 0;
                yield return update;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (preChunkFailure is null)
        {
            yield break;
        }

        if (fallbackFactory is null)
        {
            await LogDefaultFailedNoFallbackAsync(preChunkFailure).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(preChunkFailure).Throw();
            yield break;
        }

        IChatClient fallback;
        try
        {
            fallback = await GetFallbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
        {
            await LogBothFailedAsync(preChunkFailure, fallbackEx).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(preChunkFailure).Throw();
            yield break;
        }

        // Egress records the attempted provider call; activation waits until the stream starts successfully.
        RecordFallbackEgress();

        await foreach (var update in StreamFallbackAsync(fallback, messages, options, preChunkFailure, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamFallbackAsync(
        IChatClient fallback,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        Exception primaryFailure,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = fallback.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var fallbackServed = false;
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                {
                    await LogBothFailedAsync(primaryFailure, fallbackEx).ConfigureAwait(false);
                    ExceptionDispatchInfo.Capture(primaryFailure).Throw();
                    yield break;
                }

                if (!fallbackServed)
                {
                    // MoveNext can fail before yielding, so only a successful move marks the fallback as served.
                    await LogServedViaFallbackAsync(primaryFailure).ConfigureAwait(false);
                    MarkFallbackActivated();
                    fallbackServed = true;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<IChatClient> GetFallbackAsync(CancellationToken cancellationToken)
    {
        return _fallbackClient ??= await fallbackFactory!(cancellationToken).ConfigureAwait(false);
    }

    private void RecordFallbackEgress()
    {
        var context = runContextAccessor.Current;
        if (context?.EgressLedger is null)
        {
            return;
        }

        context.EgressLedger.Record(new ProviderEgressDescriptor(
            context.CurrentAgentId ?? string.Empty,
            fallbackEgressClass ?? EgressClass.Cloud,
            fallbackProviderId ?? string.Empty,
            fallbackModelId));
    }

    private void MarkFallbackActivated()
    {
        runContextAccessor.Current?.FallbackSignal?.MarkActivated();
    }

    private IDisposable? BeginUserScope()
    {
        return logger.BeginScope(new Dictionary<string, object?>
        {
            ["userId"] = userId,
            ["notificationHandled"] = true
        });
    }

    private async ValueTask LogServedViaFallbackAsync(Exception primaryFailure)
    {
        var message = $"Default model provider '{defaultProviderId}' failed; served the request via fallback provider '{fallbackProviderId}'.";
        using (BeginUserScope())
        {
            logger.LogWarning(
                primaryFailure,
                "Default model provider '{ProviderId}' failed; served the request via fallback provider '{FallbackProviderId}'.",
                defaultProviderId,
                fallbackProviderId);
        }

        await NotifyAsync(
            NotificationKind.SystemWarning,
            "Model provider fallback used",
            message).ConfigureAwait(false);
    }

    private async ValueTask LogDefaultFailedNoFallbackAsync(Exception ex)
    {
        var message = $"Default model provider '{defaultProviderId}' failed and no fallback provider is configured.";
        using (BeginUserScope())
        {
            logger.LogError(
                ex,
                "Default model provider '{ProviderId}' failed and no fallback provider is configured.",
                defaultProviderId);
        }

        await NotifyAsync(
            NotificationKind.SystemError,
            "Model provider failed",
            message).ConfigureAwait(false);
    }

    private async ValueTask LogBothFailedAsync(Exception primaryFailure, Exception fallbackFailure)
    {
        var message = $"Default model provider '{defaultProviderId}' failed and fallback provider '{fallbackProviderId}' also failed.";
        using (BeginUserScope())
        {
            logger.LogError(
                fallbackFailure,
                "Default model provider '{ProviderId}' failed and fallback provider '{FallbackProviderId}' also failed.",
                defaultProviderId,
                fallbackProviderId);
        }

        _ = primaryFailure;
        await NotifyAsync(
            NotificationKind.SystemError,
            "Model provider fallback failed",
            message).ConfigureAwait(false);
    }

    private ValueTask NotifyAsync(NotificationKind kind, string title, string message)
    {
        if (notificationMessages is null)
        {
            return ValueTask.CompletedTask;
        }

        var payload = new NotificationMessage(
            title,
            message,
            kind,
            UserId: userId);
        return new ValueTask(notificationMessages.NotifyAsync(userId, payload));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fallbackClient?.Dispose();
        }

        base.Dispose(disposing);
    }
}
