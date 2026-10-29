using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;

namespace Oscova.Agents.Core.Settings;

/// <summary>Identifies an owned project and the device whose folder it authorizes.</summary>
public sealed record ProjectDefinition(string Id, string Name, string RootPath, string? NodeId, string RootKey)
{
    /// <summary>Allows the settings serializer to restore a project from YAML.</summary>
    public ProjectDefinition() : this(string.Empty, string.Empty, string.Empty, null, string.Empty) { }
}

/// <summary>Publishes the user's project catalog only after its YAML write succeeds.</summary>
public sealed class ProjectsSettings(
    IAppSettingsRepository repository,
    string userId,
    ILoggerFactory loggerFactory) : SettingsBase(repository, userId, loggerFactory)
{
    public const string Key = "projects";
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private IReadOnlyList<ProjectDefinition> _projects = [];
    private IReadOnlyList<ProjectDefinition>? _pendingProjects;

    public override string SettingsKey => Key;

    /// <summary>Gets the committed catalog used by project authorization.</summary>
    public IReadOnlyList<ProjectDefinition> Projects => _projects;

    /// <summary>Saves an executor-validated folder, reusing its existing project on the same device.</summary>
    public async Task<ProjectDefinition> AddAsync(
        string name, string rootPath, string? nodeId, string rootKey, CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = _projects.FirstOrDefault(project =>
                string.Equals(project.NodeId, nodeId, StringComparison.Ordinal) &&
                string.Equals(project.RootKey, rootKey, StringComparison.Ordinal));
            if (existing is not null) { return existing; }

            var project = new ProjectDefinition(Guid.NewGuid().ToString(), name, rootPath, nodeId, rootKey);
            await SaveCatalogAsync(Array.AsReadOnly<ProjectDefinition>([.. _projects, project])).ConfigureAwait(false);
            return project;
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>Removes only the saved reference, leaving the folder and chats intact.</summary>
    public async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_projects.Any(project => project.Id == id)) { return false; }

            await SaveCatalogAsync(Array.AsReadOnly(_projects.Where(project => project.Id != id).ToArray())).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task SaveCatalogAsync(IReadOnlyList<ProjectDefinition> projects)
    {
        _pendingProjects = projects;
        try
        {
            await SaveAsync().ConfigureAwait(false);
            SetProperty(ref _projects, projects, nameof(Projects));
        }
        finally
        {
            _pendingProjects = null;
        }
    }

    public override string ToYaml() => Yaml.Serialize(new Snapshot
    {
        Projects = (_pendingProjects ?? _projects).ToArray()
    });

    public override void FromYaml(string yaml)
    {
        var snapshot = Yaml.Deserialize<Snapshot>(yaml);
        var projects = Array.AsReadOnly(snapshot.Projects ?? []);
        // A failed save restores its serialization state without publishing the rejected grant.
        if (_pendingProjects is not null) { _pendingProjects = projects; }
        else { SetProperty(ref _projects, projects, nameof(Projects)); }
    }

    private sealed class Snapshot
    {
        public ProjectDefinition[]? Projects { get; set; }
    }
}
