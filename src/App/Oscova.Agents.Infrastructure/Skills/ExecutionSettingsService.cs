using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Execution;
using Oscova.Agents.Infrastructure.Plugins;
using Oscova.Agents.Infrastructure.Users;

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>Checks and saves the operator's execution choice through shared configuration admission.</summary>
public sealed class ExecutionSettingsService(ExecutionSettings settings, NetworkSettings network,
    ExecutionEnvironmentCatalog environments, IUserPermissionService permissions,
    ExecutionPermissionRegistry executions, UserServiceManager users, ILogger<ExecutionSettingsService> logger)
{
    /// <summary>Requests device prerequisite checks after execution configuration admission reopens.</summary>
    public event EventHandler? Changed;
    /// <summary>Checks the selected provider using this machine's sanitized environment and current network policy.</summary>
    public async Task<ExecutionAvailabilityResult> CheckAsync(string userId, string? providerId, CancellationToken ct)
    {
        await RequireOperatorAsync(userId, ct).ConfigureAwait(false);
        if (providerId is null) { return new(true); }

        var definition = environments.Find(providerId);
        if (definition is null) { return new(false, "This environment is unavailable. Enable its plugin and restart, or select another environment."); }

        try
        {
            return await environments.GetProvider(providerId).CheckAvailabilityAsync(new(network.AllowSandboxNetworkAccess,
                definition.UsesHostPaths ? ShellExecutorFactory.CreateHostEnvironment() : new Dictionary<string, string?>()), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Execution prerequisite check failed for {ProviderId}", providerId);
            return new(false, "The execution check failed. Check the system log and this machine's prerequisites.");
        }
    }

    /// <summary>Rechecks readiness and retires earlier execution before persisting one installation-wide choice.</summary>
    public async Task SaveAsync(string userId, string? providerId, bool confirmed, CancellationToken ct)
    {
        await RequireOperatorAsync(userId, ct).ConfigureAwait(false);
        if (string.Equals(settings.ProviderId, providerId, StringComparison.Ordinal)) { return; }

        await RequireAvailableAsync(userId, providerId, ct).ConfigureAwait(false);
        var notify = true;
        try
        {
            await executions.ChangeConfigurationAsync(() => [ExecutionSettings.ConfigurationKey], async () =>
            {
                // Serialized admission keeps activation and network policy stable through validation and persistence.
                await RequireAvailableAsync(userId, providerId, ct).ConfigureAwait(false);
                settings.ProviderId = providerId;
                await settings.SaveAsync().ConfigureAwait(false);
                RefreshUsers();
            }, confirmed, ct).ConfigureAwait(false);
        }
        catch (ExecutionPermissionRegistry.ConfirmationRequiredException) { notify = false; throw; }
        finally { if (notify) { Changed?.Invoke(this, EventArgs.Empty); } }
    }

    /// <summary>Applies sandbox egress changes under the same execution authority as provider selection.</summary>
    public async Task SaveNetworkAsync(string userId, NetworkSettings.Snapshot snapshot, bool confirmed, CancellationToken ct)
    {
        await RequireOperatorAsync(userId, ct).ConfigureAwait(false);
        var changed = false;
        try
        {
            await executions.ChangeConfigurationAsync(
                () =>
                {
                    changed = network.AllowSandboxNetworkAccess != snapshot.AllowSandboxNetworkAccess;
                    return changed ? [ExecutionSettings.ConfigurationKey] : [];
                },
                async () =>
                {
                    await RequireOperatorAsync(userId, ct).ConfigureAwait(false);
                    network.ApplyFrom(snapshot);
                    await network.SaveAsync().ConfigureAwait(false);
                    RefreshUsers();
                }, confirmed, ct).ConfigureAwait(false);
        }
        catch (ExecutionPermissionRegistry.ConfirmationRequiredException) { changed = false; throw; }
        finally { if (changed) { Changed?.Invoke(this, EventArgs.Empty); } }
    }

    private async Task RequireAvailableAsync(string userId, string? providerId, CancellationToken ct)
    {
        var availability = await CheckAsync(userId, providerId, ct).ConfigureAwait(false);
        if (!availability.IsAvailable) { throw new InvalidOperationException(availability.Reason); }
    }

    private async Task RequireOperatorAsync(string userId, CancellationToken ct)
    {
        if (!await permissions.IsOperatorAsync(userId, ct).ConfigureAwait(false))
        { throw new UnauthorizedAccessException("Only the operator can change execution settings."); }
    }

    private void RefreshUsers()
    {
        foreach (var userId in users.ActiveUserIds)
        {
            if (users.TryGetExistingScope(userId, out var scope))
            {
                scope.AppService.SyncTools();
                scope.AppService.InvalidateAllCachedAgents();
            }
        }
    }
}
