using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Plugins.Registry;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Plugins.Registry;

/// <summary>Reconciles and activates official plugins with reviewed versions during startup.</summary>
public sealed class DefaultPluginInstaller(
    IPluginInstallService installer,
    PluginActivationSettings activationSettings,
    PendingPluginUpdateBootstrapper pendingUpdates,
    ILogger<DefaultPluginInstaller> logger)
{
    private const int MaxConcurrentInstalls = 4;

    /// <summary>Installs and activates missing or mismatched reviewed defaults before plugin discovery.</summary>
    public async Task InstallMissingAsync(
        IReadOnlyList<(string PackageId, string PluginId, string Version)> defaults,
        string pluginsRoot,
        Action<DefaultPluginInstallProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var installedPlugins = AgentPluginLoader.InspectDirectory(pluginsRoot)
            .Where(inspection => inspection.Diagnostic is null && inspection.Manifest is not null)
            .ToLookup(inspection => inspection.Manifest!.Id, StringComparer.OrdinalIgnoreCase);
        var pending = defaults
            .Where(item => !activationSettings.IsRemoved(item.PluginId) &&
                !installedPlugins[item.PluginId].Any(inspection =>
                    PluginFrameworkCompatibility.GetIncompatibility(inspection.Manifest!.FrameworkVersionRange,
                        PluginFrameworkCompatibility.CurrentVersion) is null &&
                    (string.Equals(inspection.Manifest.Version, item.Version, StringComparison.Ordinal) ||
                     InstalledPluginPackage.Read(inspection)?.ExplicitSelection == true)))
            .ToArray();

        // Installs run concurrently, but progress consumers and the activation settings file see one writer at a time.
        var progressGate = new Lock();
        using var activationGate = new SemaphoreSlim(1, 1);
        void Report(DefaultPluginInstallProgress progress)
        {
            lock (progressGate)
            {
                onProgress?.Invoke(progress);
            }
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentInstalls, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, pending.Length), options, async (index, ct) =>
        {
            var item = pending[index];
            var installedPlugin = installedPlugins[item.PluginId].FirstOrDefault();
            var packageId = item.PackageId;
            var installed = false;
            try
            {
                // Reconciliation changes the reviewed version while retaining the installed package edition.
                packageId = InstalledPluginPackage.Read(installedPlugin)?.PackageId ?? item.PackageId;
                Report(new DefaultPluginInstallProgress(
                    packageId, index + 1, pending.Length, DefaultPluginInstallState.Installing));

                var installedId = await installer.InstallAsync(
                    packageId,
                    item.Version,
                    item.PluginId,
                    ct, explicitSelection: false).ConfigureAwait(false);

                if (installedPlugin is null)
                {
                    await activationGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var enabledIds = activationSettings.EnabledPluginIds
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        enabledIds.Add(installedId);
                        activationSettings.SetEnabledPluginIds(enabledIds);
                        try
                        {
                            await activationSettings.SaveAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            // Keep failed activation retryable as a missing default on the next startup.
                            installer.Uninstall(installedId);
                            throw;
                        }
                    }
                    finally
                    {
                        activationGate.Release();
                    }
                }

                logger.LogDebug(
                    "Reconciled default plugin {PluginId} from package {PackageId}.",
                    installedId,
                    packageId);
                installed = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex,
                    "Failed to install or enable default plugin package {PackageId}.",
                    packageId);
            }

            Report(new DefaultPluginInstallProgress(
                packageId, index + 1, pending.Length,
                installed ? DefaultPluginInstallState.Installed : DefaultPluginInstallState.Failed));
        }).ConfigureAwait(false);

        // Startup has not loaded plugin assemblies, so reviewed replacements can become canonical now.
        pendingUpdates.ApplyPendingUpdates();
    }
}
