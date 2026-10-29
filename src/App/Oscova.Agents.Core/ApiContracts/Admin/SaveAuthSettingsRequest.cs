using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Core.ApiContracts.Admin;

/// <summary>Sets the authentication mode for the application.</summary>
public sealed class SaveAuthSettingsRequest
{
    public AuthMode Mode { get; set; }
}
