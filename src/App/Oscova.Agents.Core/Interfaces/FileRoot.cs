namespace Oscova.Agents.Core.Interfaces;

/// <summary>Selects the folder a Files operation resolves against.</summary>
public enum FileRoot
{
    /// <summary>The signed-in user's own folder.</summary>
    Home,

    /// <summary>The host-wide folder shared by permitted users.</summary>
    Shared
}
