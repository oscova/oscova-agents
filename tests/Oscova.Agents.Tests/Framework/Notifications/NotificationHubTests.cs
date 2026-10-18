using Microsoft.Extensions.Logging.Abstractions;
using Oscova.Agents.Notifications;

namespace Oscova.Agents.Tests.Framework.Notifications;

public sealed class NotificationHubTests
{
    [Fact]
    public async Task ConcurrentSubscriptionReplacement_RetainsTheNewHandler()
    {
        var hub = new NotificationHub<int>(NullLogger<NotificationHub<int>>.Instance);
        using var barrier = new Barrier(2);
        for (int iteration = 0; iteration < 5_000; iteration++)
        {
            using var previous = hub.Subscribe("user", _ => Task.CompletedTask);
            int received = 0;
            IDisposable? replacement = null;
            try
            {
                await Task.WhenAll(
                    Task.Run(() =>
                    {
                        barrier.SignalAndWait();
                        previous.Dispose();
                    }),
                    Task.Run(() =>
                    {
                        barrier.SignalAndWait();
                        replacement = hub.Subscribe("user", value =>
                        {
                            received += value;
                            return Task.CompletedTask;
                        });
                    })).WaitAsync(TimeSpan.FromSeconds(10));

                await hub.NotifyAsync("other-user", 1);
                Assert.Equal(0, received);
                await hub.NotifyAsync("user", 1);
                Assert.Equal(1, received);
            }
            finally
            {
                replacement?.Dispose();
            }
        }
    }
}
