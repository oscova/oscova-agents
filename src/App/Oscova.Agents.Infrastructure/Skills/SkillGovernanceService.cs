using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Secrets;
using Oscova.Agents.Settings;
using Oscova.Agents.Skills;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>Owns shared skill publication and the managed mutation boundary for recipient copies.</summary>
public sealed class SkillGovernanceService(
    IUserPermissionService permissions,
    IAppSettingsRepository repository,
    IContentProtector protector,
    IDbContextFactory<ApplicationDbContext> contexts,
    ExecutionPermissionRegistry executions,
    SkillImportService importer,
    Func<string, string> skillsRoot,
    ILoggerFactory logs)
{
    private readonly SemaphoreSlim _load = new(1, 1);
    private SharedSkillSettings? _catalog;
    private string? _operatorId;

    /// <summary>Identifies a published package independently of any recipient's filesystem path.</summary>
    public static string ResourceKey(string name) => "skill/shared/" + name.ToLowerInvariant();

    private async Task<SharedSkillSettings> GetCatalogAsync(CancellationToken ct)
    {
        var operatorId = await permissions.GetOperatorIdAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Operator setup is required.");
        if (!protector.IsAvailable(operatorId)) { throw new InvalidOperationException("The operator must unlock the vault."); }

        await _load.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalog is null)
            {
                var catalog = new SharedSkillSettings(repository, operatorId, logs);
                await catalog.LoadAsync().ConfigureAwait(false);
                _operatorId = operatorId;
                _catalog = catalog;
            }

            return _catalog;
        }
        finally { _load.Release(); }
    }

    /// <summary>Returns published names without exposing package contents or the operator's paths.</summary>
    public async Task<IReadOnlyList<string>> GetSharedNamesAsync(string userId, CancellationToken ct = default)
    {
        var catalog = await GetCatalogAsync(ct).ConfigureAwait(false);
        return catalog.Packages.Keys.Where(name => userId == _operatorId || IsRecipient(catalog, userId, name)).ToArray();
    }

    private static bool IsRecipient(SharedSkillSettings catalog, string userId, string name) =>
        catalog.Recipients.TryGetValue(name, out var ids) && ids.Contains(userId, StringComparer.Ordinal);

    /// <summary>Applies the custom-management grant to uploads, authoring and managed file edits.</summary>
    public async Task RequireEditableAsync(string userId, string? name, CancellationToken ct)
    {
        if (await permissions.IsOperatorAsync(userId, ct).ConfigureAwait(false)) { return; }

        await permissions.RequireAsync(userId, UserPermissionKeys.ManageCustomSkills, ct).ConfigureAwait(false);
        var catalog = await GetCatalogAsync(ct).ConfigureAwait(false);
        if (name is not null && IsRecipient(catalog, userId, name))
        {
            throw new UnauthorizedAccessException("This skill is managed by the operator and is read-only.");
        }
    }

    /// <summary>Checks skill mutations reached through the generic user-file APIs.</summary>
    public async Task RequirePathEditableAsync(string userId, string path, CancellationToken ct = default)
    {
        var root = skillsRoot(userId);
        if (!PathHelper.IsPathWithinDirectory(path, root)) { return; }

        if (PathHelper.ArePathsEqual(path, root) && !await permissions.IsOperatorAsync(userId, ct).ConfigureAwait(false) &&
            (await GetSharedNamesAsync(userId, ct).ConfigureAwait(false)).Count > 0)
        {
            throw new UnauthorizedAccessException("The skills folder contains operator-managed skills and cannot be removed.");
        }

        var relative = PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(root, path));
        await RequireEditableAsync(userId, relative.Split('/')[0], ct).ConfigureAwait(false);
    }

    /// <summary>Copies the last published versions into a new account without reading private operator edits.</summary>
    public async Task EnsureRecipientAsync(string userId, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(ct).ConfigureAwait(false);
        if (userId == _operatorId) { return; }

        await _load.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (name, package) in catalog.Packages)
            {
                catalog.Recipients.TryGetValue(name, out var ids);
                if (Directory.Exists(Path.Combine(skillsRoot(userId), name)))
                {
                    // Existing personal skills retain their contents and ownership.
                    continue;
                }

                var key = ResourceKey(name);
                using var registration = executions.RegisterConfiguration(key, executions.GetConfigurationVersion(key), userId, ct);
                using var content = new MemoryStream(package, writable: false);
                await importer.ImportTrustedPackageAsync(userId, name, content, false, registration.Token).ConfigureAwait(false);
                var previous = catalog.Recipients;
                var recipients = previous.ToDictionary();
                recipients[name] = [.. ids ?? [], userId];
                try
                {
                    catalog.Replace(catalog.Packages, recipients);
                    await catalog.SaveAsync().ConfigureAwait(false);
                }
                catch
                {
                    catalog.Replace(catalog.Packages, previous);
                    importer.RemoveTrustedPackage(userId, name);
                    throw;
                }
            }
        }
        finally { _load.Release(); }
    }

    /// <summary>Captures the publication version when a skill is discovered, without registering active use.</summary>
    public async Task<(string Key, long Version)?> GetDependencyAsync(string userId, string name, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(ct).ConfigureAwait(false);
        if (userId == _operatorId || !IsRecipient(catalog, userId, name)) { return null; }

        var key = ResourceKey(name);
        return (key, executions.GetConfigurationVersion(key));
    }

    /// <summary>Publishes, updates or removes recipient copies after affected work has stopped.</summary>
    public async Task<Oscova.Agents.Core.ApiContracts.Agents.SkillPublicationResult> PublishAsync(string callerId, string name, bool enabled, bool confirmed, CancellationToken ct)
    {
        if (!await permissions.IsOperatorAsync(callerId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Only the operator can publish skills.");
        }

        var catalog = await GetCatalogAsync(ct).ConfigureAwait(false);
        // Export only this selected package. Later private edits do not alter the published bytes.
        var package = enabled ? await importer.ExportPackageAsync(callerId, name, ct).ConfigureAwait(false) : null;
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var recipients = await db.Users.AsNoTracking()
            .Where(user => user.Id != callerId && user.Id != Core.WellKnownUsers.SystemId)
            .Select(user => new { user.Id, user.UserName }).ToArrayAsync(ct).ConfigureAwait(false);
        var changed = 0;
        var preserved = 0;
        await executions.ChangeConfigurationAsync(() =>
        {
            if (!enabled && !catalog.Packages.ContainsKey(name))
            {
                throw new InvalidOperationException("This skill has not been published.");
            }

            return [ResourceKey(name)];
        }, async () =>
        {
            await _load.WaitAsync(ct).ConfigureAwait(false);
            var previous = catalog.Packages.ToDictionary();
            var previousRecipients = catalog.Recipients;
            previous.TryGetValue(name, out var previousPackage);
            var applied = new List<string>();
            try
            {
                foreach (var user in recipients)
                {
                    ct.ThrowIfCancellationRequested();
                    var owned = previousRecipients.TryGetValue(name, out var ids) && ids.Contains(user.Id, StringComparer.Ordinal);
                    if (package is null)
                    {
                        if (!owned) { continue; }

                        importer.RemoveTrustedPackage(user.Id, name);
                    }
                    else
                    {
                        if (!owned && Directory.Exists(Path.Combine(skillsRoot(user.Id), name)))
                        {
                            preserved++;
                            continue;
                        }

                        using var content = new MemoryStream(package, writable: false);
                        await importer.ImportTrustedPackageAsync(user.Id, name, content,
                            owned, ct).ConfigureAwait(false);
                    }

                    applied.Add(user.Id);
                }

                var replacement = previous.ToDictionary();
                var owners = previousRecipients.ToDictionary();
                if (package is null) { replacement.Remove(name); }
                else { replacement[name] = package; }

                if (package is null) { owners.Remove(name); }
                else { owners[name] = applied.ToArray(); }

                catalog.Replace(replacement, owners);
                await catalog.SaveAsync().ConfigureAwait(false);
                changed = applied.Count;
            }
            catch (Exception failure)
            {
                catalog.Replace(previous, previousRecipients);
                var errors = new List<Exception> { failure };
                foreach (var userId in applied)
                {
                    try
                    {
                        var owned = previousRecipients.TryGetValue(name, out var ids) && ids.Contains(userId, StringComparer.Ordinal);
                        if (previousPackage is null || !owned) { importer.RemoveTrustedPackage(userId, name); }
                        else
                        {
                            using var content = new MemoryStream(previousPackage, writable: false);
                            await importer.ImportTrustedPackageAsync(userId, name, content,
                                Directory.Exists(Path.Combine(skillsRoot(userId), name)), CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception rollback) { errors.Add(rollback); }
                }

                logs.CreateLogger<SkillGovernanceService>().LogError(failure,
                    "Shared skill publication failed for {SkillName}. Cleanup failures: {Count}", name, errors.Count - 1);
                if (errors.Count > 1) { throw new AggregateException("Skill publication and recipient restoration failed. Review the server log before retrying.", errors); }

                throw;
            }
            finally { _load.Release(); }
        }, confirmed, ct).ConfigureAwait(false);
        return new(changed, preserved);
    }
}
