using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.ApiContracts.Skills;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Skills;
using Oscova.Agents.Core.Skills.Registry;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Skills;

namespace Oscova.Agents.Infrastructure.Skills.Registry;

/// <summary>
/// Downloads the files of a remote skill, packs them into an in-memory zip, and delegates to <see cref="ISkillImportService"/>.
/// </summary>
public sealed class GitHubSkillInstallService(
    IGitHubSkillRegistryService registry,
    ISkillImportService skillImport,
    Oscova.Agents.Core.Interfaces.IUserPermissionService permissions,
    Oscova.Agents.Core.Settings.SkillRegistrySettings settings,
    ILogger<GitHubSkillInstallService> logger) : IGitHubSkillInstallService
{
    public async Task<ImportedSkillResult> InstallAsync(
        string userId,
        SkillRegistrySource source,
        string skillName,
        string skillPath,
        IProgress<SkillInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await permissions.RequireAsync(userId, Core.Auth.UserPermissionKeys.InstallApprovedSkills,
            cancellationToken).ConfigureAwait(false);
        source = settings.FindSource(source.Owner, source.Repo, source.Branch)
            ?? throw new UnauthorizedAccessException("This skill repository is not enabled by the operator.");
        // One install retains its acquisition choice even if the operator saves another method.
        var searchMethod = settings.SearchMethod;
        var catalog = await registry.EnumerateSkillsAsync(source, cancellationToken, searchMethod: searchMethod).ConfigureAwait(false);
        if (!catalog.Entries.Any(candidate =>
            string.Equals(candidate.SkillName, skillName, StringComparison.Ordinal) &&
            string.Equals(candidate.SkillPath, skillPath, StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException("Select a skill from the operator-approved repository catalog.");
        }

        progress?.Report(new SkillInstallProgress(SkillInstallStage.Resolving, 0, null));

        var entry = new RemoteSkillCatalogEntry
        {
            Source = source,
            SkillName = skillName,
            SkillPath = skillPath
        };

        var files = await registry.ListSkillFilesAsync(entry, cancellationToken, searchMethod).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new GitHubRegistryException($"No files found at {source.DisplayName}/{skillPath}.");
        }

        var totalBytes = files.Sum(f => f.Size);
        if (totalBytes > SkillImportLimits.MaxUncompressedZipSizeBytes)
        {
            throw new ArgumentException(
                $"The remote skill exceeds the {ByteSize.Format(SkillImportLimits.MaxUncompressedZipSizeBytes)} uncompressed size limit.");
        }

        progress?.Report(new SkillInstallProgress(SkillInstallStage.Downloading, 0, totalBytes));

        var zipStream = new MemoryStream();
        try
        {
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                long downloaded = 0;
                foreach (var file in files)
                {
                    var relative = file.RepoPath.StartsWith(skillPath + "/", StringComparison.Ordinal)
                        ? file.RepoPath[(skillPath.Length + 1)..]
                        : file.RepoPath;

                    // Zip entries always use forward slashes regardless of host OS.
                    var entryName = $"{skillName}/{relative}";
                    var zipEntry = zip.CreateEntry(entryName, CompressionLevel.Optimal);

                    var entryStream = zipEntry.Open();
                    await using (entryStream.ConfigureAwait(false))
                    {
                        void ReportProgress(long bytes)
                        {
                            downloaded += bytes;
                            progress?.Report(new SkillInstallProgress(SkillInstallStage.Downloading, downloaded, totalBytes));
                        }

                        await registry.DownloadFileAsync(
                            file,
                            entryStream,
                            SkillImportLimits.MaxUncompressedZipSizeBytes - downloaded,
                            ReportProgress,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            zipStream.Position = 0;

            progress?.Report(new SkillInstallProgress(SkillInstallStage.Importing, totalBytes, totalBytes));
            var result = await skillImport.ImportAsync(userId, $"{skillName}.zip", zipStream, cancellationToken).ConfigureAwait(false);
            progress?.Report(new SkillInstallProgress(SkillInstallStage.Installed, totalBytes, totalBytes));

            logger.LogInformation("Installed remote skill {Source}/{Path} for user {UserId}.", source.DisplayName, skillPath, userId);
            return SkillImportResultMapper.ToImportedSkillResult(result);
        }
        finally
        {
            await zipStream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
