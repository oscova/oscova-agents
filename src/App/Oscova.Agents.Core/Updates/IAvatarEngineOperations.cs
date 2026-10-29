using Oscova.Agents.Core.ApiContracts.Updates;

namespace Oscova.Agents.Core.Updates;

/// <summary>Lets the Host coordinate App-owned Avatar operations without referencing the launcher.</summary>
public interface IAvatarEngineOperations
{
    /// <summary>Reads local installation and tracked process state.</summary>
    Task<AvatarEngineStatus> GetStatusAsync(CancellationToken cancellationToken);
    /// <summary>Discovers publication even when no local installation exists or the platform cannot install it.</summary>
    Task<AvatarReleaseIdentity> GetPublishedAsync(CancellationToken cancellationToken);
    /// <summary>Replaces confirmed content under one lifecycle lease and restores the prior running state.</summary>
    Task InstallOrUpdateAsync(AvatarReleaseIdentity accepted, Action<AvatarDownloadProgress> progress, CancellationToken cancellationToken);
    /// <summary>Starts a managed local process using the existing validated launch arguments.</summary>
    Task<AvatarEngineRuntime> StartAsync(string address, int port, CancellationToken cancellationToken);
}
