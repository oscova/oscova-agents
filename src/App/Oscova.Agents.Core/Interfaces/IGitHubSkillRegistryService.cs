using Oscova.Agents.Core.ApiContracts.Skills.Registry;
using Oscova.Agents.Core.Skills.Registry;

namespace Oscova.Agents.Core.Interfaces;

/// <summary>Discovers and reads configured GitHub skills using the operator's acquisition choice.</summary>
public interface IGitHubSkillRegistryService
{
    /// <summary>Returns discovered skills and reports preparation of their cached source.</summary>
    Task<SkillRegistryCatalogResponse> EnumerateSkillsAsync(
        SkillRegistrySource source, CancellationToken cancellationToken,
        Action<SkillRegistryPreparationProgress>? onProgress = null,
        SkillRegistrySearchMethod? searchMethod = null);

    /// <summary>Reads only the selected skill's frontmatter description.</summary>
    Task<string?> FetchDescriptionAsync(RemoteSkillCatalogEntry entry, CancellationToken cancellationToken);

    /// <summary>Lists regular files below a skill folder at one cached revision.</summary>
    Task<IReadOnlyList<RemoteSkillFile>> ListSkillFilesAsync(
        RemoteSkillCatalogEntry entry, CancellationToken cancellationToken, SkillRegistrySearchMethod? searchMethod = null);

    /// <summary>Copies a listed immutable file into a bounded destination stream.</summary>
    Task DownloadFileAsync(RemoteSkillFile file, Stream destination, long maxBytes, Action<long>? progress, CancellationToken cancellationToken);
}

/// <summary>Reports a repository acquisition or cached-content failure.</summary>
public sealed class GitHubRegistryException : Exception
{
    public GitHubRegistryException(string message) : base(message) { }
    public GitHubRegistryException(string message, Exception innerException) : base(message, innerException) { }
}
