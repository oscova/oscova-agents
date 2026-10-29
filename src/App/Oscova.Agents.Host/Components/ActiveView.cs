namespace Oscova.Agents.Host.Components;

public enum ActiveView
{
    Dashboard,
    Achievements,
    Chat,
    Settings,
    Admin,
    Tasks,

    // Pages outside the main navigation, such as not-found, so no nav item is marked current.
    None
}
