namespace Oscova.Agents.Core.ApiContracts.Projects;

/// <summary>Requests authorization for one existing folder on the selected execution device.</summary>
public sealed record CreateProjectRequest(string Name, string RootPath, string? NodeId);

/// <summary>Returns a root validated and canonicalized by the device that owns it.</summary>
public sealed record ValidatedProjectRoot(string Path, string Key);

/// <summary>Describes a selectable folder without accepting host paths in file requests.</summary>
public sealed record ProjectFileRoot(string Id, string Name, bool IsProject, bool IsPrivate, bool IsReadOnly);

/// <summary>Describes a direct child of the selected folder.</summary>
public sealed record ProjectFileEntry(string Name, string Path, bool IsDirectory, long Length, string FullPath, string AgentPath);

/// <summary>Returns a bounded listing and whether narrower browsing is needed.</summary>
public sealed record ProjectFileListing(IReadOnlyList<ProjectFileEntry> Entries, bool Truncated);

/// <summary>Distinguishes displayable text from status-only file and repository responses.</summary>
public enum ProjectReadState { Ready, Binary, TooLarge, Unavailable, NotRepository, GitMissing, Unsupported, RefreshRequired }

/// <summary>Returns bounded text, current editing access and its stored format.</summary>
public sealed record ProjectFilePreview(ProjectReadState State, string Text = "", string? Revision = null,
    bool CanEdit = false, bool HasUtf8Bom = false, string NewLine = "\n");

/// <summary>Replaces existing text only when the originally loaded stored revision still matches.</summary>
public sealed record SaveProjectFileRequest(string Text, string ExpectedRevision);

/// <summary>Reports a confirmed save or the current revision requiring explicit conflict recovery.</summary>
public sealed record ProjectFileSaveResult(string Revision, bool Conflict = false);

/// <summary>Identifies one staged, unstaged, or untracked repository comparison.</summary>
public sealed record ProjectChange(string Path, string? OldPath, string Status, string Group);

/// <summary>Returns repository state independently of the conversation's editing history.</summary>
public sealed record ProjectChanges(ProjectReadState State, IReadOnlyList<ProjectChange> Entries, bool Truncated = false);

/// <summary>Returns the exact bounded sides of one selected repository comparison.</summary>
public sealed record ProjectChangePreview(ProjectReadState State, string OldText = "", string NewText = "");
