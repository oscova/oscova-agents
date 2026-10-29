namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Supplies the user-selected title for one chat thread.</summary>
public sealed class RenameThreadRequest
{
    public const int MaxTitleLength = 120;

    /// <summary>Gets or sets the requested conversation title.</summary>
    public string? Title { get; init; }
}
