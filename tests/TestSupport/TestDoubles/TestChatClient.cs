using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace Oscova.Agents.Tests.Infrastructure.TestDoubles;

/// <summary>
/// Scriptable chat client for unit tests that need to inspect requests or return deterministic responses.
/// </summary>
public sealed class TestChatClient : IChatClient
{
    private readonly List<IReadOnlyList<ChatMessage>> _responseMessages = [];
    private readonly List<ChatOptions?> _responseOptions = [];

    /// <summary>
    /// Gets or sets the scripted non-streaming response callback.
    /// </summary>
    public Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? ResponseAsync { get; init; }

    /// <summary>
    /// Gets or sets the scripted streaming response callback.
    /// </summary>
    public Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? StreamingAsync { get; init; }

    /// <summary>
    /// Gets the number of non-streaming calls received by the client.
    /// </summary>
    public int ResponseCalls { get; private set; }

    /// <summary>
    /// Gets the number of streaming calls received by the client.
    /// </summary>
    public int StreamingCalls { get; private set; }

    /// <summary>
    /// Gets snapshots of messages passed to non-streaming calls.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> ResponseMessages => _responseMessages;

    /// <summary>
    /// Gets snapshots of options passed to non-streaming calls.
    /// </summary>
    public IReadOnlyList<ChatOptions?> ResponseOptions => _responseOptions;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ResponseCalls++;
        var messageList = messages.ToList();
        _responseMessages.Add(messageList);
        _responseOptions.Add(options);
        return ResponseAsync?.Invoke(messageList, options, cancellationToken)
            ?? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        StreamingCalls++;
        return StreamingAsync?.Invoke(messages.ToList(), options, cancellationToken)
            ?? StreamAsync([], cancellationToken);
    }

    /// <summary>
    /// Creates an async stream from a fixed set of chat response updates.
    /// </summary>
    public static async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IReadOnlyList<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return update;
        }
    }
}
