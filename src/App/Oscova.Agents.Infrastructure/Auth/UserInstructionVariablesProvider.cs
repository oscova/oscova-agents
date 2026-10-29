using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Settings;

namespace Oscova.Agents.Infrastructure.Auth;

public sealed class UserInstructionVariablesProvider(
    string displayName,
    string userName,
    string userEmail,
    AssistantUiSettings uiSettings,
    UserProfileSettings profileSettings) : IUserInstructionVariablesProvider
{
    private string _displayName = displayName;
    private string _userName = userName;
    private string _userEmail = userEmail;

    public Task<UserInstructionVariables> GetVariablesAsync(string userId, CancellationToken ct = default)
    {
        var resolvedDisplayName = !string.IsNullOrWhiteSpace(_displayName)
            ? _displayName
            : _userName;

        return Task.FromResult(new UserInstructionVariables(
            string.IsNullOrWhiteSpace(resolvedDisplayName) ? "User" : resolvedDisplayName,
            profileSettings.Nickname,
            _userName,
            _userEmail,
            uiSettings.GetTimeZone()));
    }

    /// <summary>
    /// Refreshes the profile values injected into newly rendered agent instructions.
    /// </summary>
    internal void RefreshProfile(string displayName, string userName, string userEmail)
    {
        _displayName = displayName;
        _userName = userName;
        _userEmail = userEmail;
    }
}
