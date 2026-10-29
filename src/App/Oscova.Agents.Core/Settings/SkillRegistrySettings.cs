using Oscova.Agents.Utils;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;
using Oscova.Agents.Core.Skills.Registry;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// System-wide list of GitHub repositories that act as skill registries for the "Find skills online" feature.
/// </summary>
public sealed class SkillRegistrySettings : SettingsBase
{
    public const string Key = "skill-registry";
    private SkillRegistrySearchMethod _searchMethod;

    /// <summary>Controls acquisition for everyone using the configured skill sources.</summary>
    public SkillRegistrySearchMethod SearchMethod
    {
        get => _searchMethod;
        set => SetProperty(ref _searchMethod, value);
    }

    private List<SkillRegistrySource> _sources =
    [
        new() { Owner = "github", Repo = "awesome-copilot", Branch = "main", SkillRoot = "skills" },
        new() { Owner = "sickn33", Repo = "agentic-awesome-skills", Branch = "main", SkillRoot = "skills" },
        new() { Owner = "coreyhaines31", Repo = "marketingskills", Branch = "main", SkillRoot = "skills" },
        new() { Owner = "K-Dense-AI", Repo = "scientific-agent-skills", Branch = "main", SkillRoot = "skills" }
    ];

    public SkillRegistrySettings(IAppSettingsRepository repository, ILoggerFactory loggerFactory)
        : base(repository, WellKnownUsers.SystemId, loggerFactory) { }

    public override string SettingsKey => Key;

    public IReadOnlyList<SkillRegistrySource> Sources => _sources;

    /// <summary>
    /// Returns the configured source matching owner, repository, and branch, or null when it is not allowed.
    /// </summary>
    public SkillRegistrySource? FindSource(string owner, string repo, string? branch)
    {
        var normalizedOwner = owner.Trim();
        var normalizedRepo = repo.Trim();
        var matches = Sources
            .Where(source =>
                string.Equals(source.Owner, normalizedOwner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(source.Repo, normalizedRepo, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (string.IsNullOrWhiteSpace(branch))
        {
            return matches.Count == 1 ? matches[0] : null;
        }

        var normalizedBranch = branch.Trim();

        return matches.FirstOrDefault(source =>
            string.Equals(source.Branch, normalizedBranch, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Replaces the configured sources with the supplied list, trimming and de-duplicating by owner/repo/branch.
    /// </summary>
    public void ReplaceSources(IEnumerable<SkillRegistrySource> sources)
    {
        var deduped = new List<SkillRegistrySource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var owner = source.Owner?.Trim() ?? string.Empty;
            var repo = source.Repo?.Trim() ?? string.Empty;
            var branch = string.IsNullOrWhiteSpace(source.Branch) ? "main" : source.Branch.Trim();
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)) { continue; }

            if (!seen.Add($"{owner}/{repo}@{branch}")) { continue; }

            deduped.Add(new SkillRegistrySource
            {
                Owner = owner,
                Repo = repo,
                Branch = branch,
                SkillRoot = PathHelper.ToSlashSeparatedRelativePath((source.SkillRoot ?? string.Empty).Trim()).Trim('/')
            });
        }

        _sources = deduped;
        OnPropertyChanged(nameof(Sources));
        OnChanged();
    }

    /// <summary>Returns a detached settings snapshot for editing and persistence.</summary>
    public Snapshot CreateSnapshot()
    {
        return new Snapshot
        {
            SearchMethod = SearchMethod,
            Sources = _sources.ConvertAll(source => new SkillRegistrySource
            {
                Owner = source.Owner,
                Repo = source.Repo,
                Branch = source.Branch,
                SkillRoot = source.SkillRoot
            })
        };
    }

    public void ApplyFrom(Snapshot data)
    {
        SearchMethod = data.SearchMethod;
        ReplaceSources(data.Sources ?? []);
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(CreateSnapshot());
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is not null)
        {
            ApplyFrom(data);
        }
    }

    public sealed class Snapshot
    {
        public SkillRegistrySearchMethod SearchMethod { get; set; }
        public IList<SkillRegistrySource> Sources { get; set; } = [];
    }
}
