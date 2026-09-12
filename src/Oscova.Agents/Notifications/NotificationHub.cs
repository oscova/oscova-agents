using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Notifications;

/// <summary>
/// Dispatches typed notifications to user-specific and global subscribers.
/// </summary>
/// <typeparam name="T">The notification payload type.</typeparam>
/// <param name="logger">The logger used when subscriber callbacks fail.</param>
public sealed class NotificationHub<T>(ILogger<NotificationHub<T>> logger)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Func<T, Task>>> _subscriptions =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Func<T, Task>> _globalSubscriptions = new();
    private readonly ConcurrentDictionary<long, Func<string?, T, Task>> _globalContextSubscriptions = new();
    private long _nextContextSubscriptionId;

    /// <summary>
    /// Subscribes a handler to notifications for one user.
    /// </summary>
    /// <param name="userId">The user whose notifications should be received.</param>
    /// <param name="handler">The asynchronous callback to invoke for matching notifications.</param>
    /// <returns>A disposable subscription that removes the handler.</returns>
    public IDisposable Subscribe(string userId, Func<T, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(handler);

        var subscriptionId = Guid.NewGuid();
        var handlers = _subscriptions.GetOrAdd(userId, static _ => []);
        handlers[subscriptionId] = handler;

        return new Subscription(() =>
        {
            if (_subscriptions.TryGetValue(userId, out var userHandlers))
            {
                userHandlers.TryRemove(subscriptionId, out _);
                if (userHandlers.IsEmpty)
                {
                    _subscriptions.TryRemove(userId, out _);
                }
            }
        });
    }

    /// <summary>
    /// Subscribes a handler to all notifications without user context.
    /// </summary>
    /// <param name="handler">The asynchronous callback to invoke for every notification.</param>
    /// <returns>A disposable subscription that removes the handler.</returns>
    public IDisposable SubscribeAll(Func<T, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscriptionId = Guid.NewGuid();
        _globalSubscriptions[subscriptionId] = handler;

        return new Subscription(() => _globalSubscriptions.TryRemove(subscriptionId, out _));
    }

    /// <summary>
    /// Subscribes a handler to all notifications with optional user context.
    /// </summary>
    /// <param name="handler">The asynchronous callback to invoke with the target user when available.</param>
    /// <returns>A disposable subscription that removes the handler.</returns>
    public IDisposable SubscribeAll(Func<string?, T, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscriptionId = Interlocked.Increment(ref _nextContextSubscriptionId);
        _globalContextSubscriptions[subscriptionId] = handler;

        return new Subscription(() => _globalContextSubscriptions.TryRemove(subscriptionId, out _));
    }

    /// <summary>
    /// Sends a notification to one user and all global subscribers.
    /// </summary>
    /// <param name="userId">The target user identifier.</param>
    /// <param name="payload">The notification payload.</param>
    /// <returns>A task that completes after subscribed handlers have been invoked.</returns>
    public Task NotifyAsync(string userId, T payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return NotifyHandlersAsync(GetHandlers(userId), userId, payload);
    }

    /// <summary>
    /// Sends a notification to every user-specific and global subscriber.
    /// </summary>
    /// <param name="payload">The notification payload.</param>
    /// <returns>A task that completes after subscribed handlers have been invoked.</returns>
    public Task NotifyAllAsync(T payload)
    {
        var handlers = _globalSubscriptions.Values.ToList();
        foreach (var userHandlers in _subscriptions.Values)
        {
            handlers.AddRange(userHandlers.Values);
        }

        return NotifyHandlersAsync(handlers, null, payload);
    }

    private List<Func<T, Task>> GetHandlers(string userId)
    {
        var handlers = _globalSubscriptions.Values.ToList();
        if (_subscriptions.TryGetValue(userId, out var userHandlers))
        {
            handlers.AddRange(userHandlers.Values);
        }

        return handlers;
    }

    private async Task NotifyHandlersAsync(IEnumerable<Func<T, Task>> handlers, string? userId, T payload)
    {
        // Later handlers can observe state created by contextual handlers that registered earlier.
        var contextualHandlers = _globalContextSubscriptions
            .OrderBy(subscription => subscription.Key)
            .Select(subscription => subscription.Value);

        foreach (var handler in contextualHandlers)
        {
            try
            {
                await handler(userId, payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Contextual notification handler for {NotificationType} failed.", typeof(T).Name);
            }
        }

        foreach (var handler in handlers)
        {
            try
            {
                await handler(payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Notification handler for {NotificationType} failed.", typeof(T).Name);
            }
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            unsubscribe();
        }
    }
}
