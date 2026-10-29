namespace Oscova.Agents.Core.Library;

/// <summary>Contains one authorized semantic match from the user's Library.</summary>
public sealed record LibrarySearchResult(
    string WorkspacePath,
    string Content,
    int? PageNumber = null);
