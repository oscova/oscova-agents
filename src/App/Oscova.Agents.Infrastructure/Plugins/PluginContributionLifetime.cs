using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Infrastructure.Plugins;

/// <summary>Releases Host-created plugin contributions once, including partial initialization.</summary>
internal sealed class PluginContributionLifetime(ILogger logger) : IAsyncDisposable
{
    private readonly List<object> _owned = [];
    public bool Contains(object contribution) => _owned.Contains(contribution, ReferenceEqualityComparer.Instance);
    public void Add(object contribution)
    {
        if ((contribution is IDisposable or IAsyncDisposable) && !Contains(contribution))
        {
            _owned.Add(contribution);
        }
    }
    public ValueTask DisposeAsync() => ReleaseAsync(_owned.ToArray());

    /// <summary>Releases retired contributions without retaining them until the device exits.</summary>
    public async ValueTask ReleaseAsync(IEnumerable<object> contributions)
    {
        var owned = contributions.Where(_owned.Remove).ToArray();
        for (var index = owned.Length - 1; index >= 0; index--)
        {
            try
            {
                if (owned[index] is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (owned[index] is IDisposable disposable) { disposable.Dispose(); }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Plugin contribution {ContributionType} failed to dispose.", owned[index].GetType().FullName);
            }
        }
    }
}
