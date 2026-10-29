namespace Oscova.Agents.Host;

/// <summary>
/// Marker localizer anchor types must remain in the web root namespace so IStringLocalizer{T}
/// resolves the existing Resources/*.resx base names.
/// </summary>

/// <summary>
/// Marker type for shared common UI resource strings.
/// </summary>
public sealed class CommonText;

/// <summary>
/// Marker type for authentication and first-run setup resource strings.
/// </summary>
public sealed class AuthText;

/// <summary>
/// Marker type for shell and navigation resource strings.
/// </summary>
public sealed class NavigationText;

/// <summary>
/// Marker type for achievement resource strings.
/// </summary>
public sealed class AchievementText;

/// <summary>
/// Marker type for settings resource strings.
/// </summary>
public sealed class SettingsText;

/// <summary>
/// Marker type for chat-specific resource strings.
/// </summary>
public sealed class ChatText;

/// <summary>
/// Marker type for dynamic chat start-screen opening strings.
/// </summary>
public sealed class ChatStartText;

/// <summary>
/// Marker type for first-run onboarding wizard resource strings.
/// </summary>
public sealed class WizardText;

/// <summary>
/// Marker type for Discord/Telegram elicitation prompt and reply strings.
/// </summary>
public sealed class ElicitationText;
