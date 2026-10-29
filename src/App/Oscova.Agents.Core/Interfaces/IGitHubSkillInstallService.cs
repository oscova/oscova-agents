using Oscova.Agents.Core.ApiContracts.Skills;
using Oscova.Agents.Core.Skills.Registry;

namespace Oscova.Agents.Core.Interfaces;

/// <summary>
/// Builds an in-memory zip of a remote skill and hands it to the standard skill import pipeline.
/// </summary>
public interface IGitHubSkillInstallService
{
    Task<ImportedSkillResult> InstallAsync(
        string userId,
        SkillRegistrySource source,
        string skillName,
        string skillPath,
        IProgress<SkillInstallProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Progress sample emitted while a remote skill installation is running.
/// </summary>
public sealed record SkillInstallProgress(SkillInstallStage Stage, long BytesReceived, long? TotalBytes);

public enum SkillInstallStage
{
    Resolving,
    Downloading,
    Importing,
    Installed
}
