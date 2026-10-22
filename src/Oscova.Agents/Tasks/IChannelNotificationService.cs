using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Hands off a task session to a specific channel and notifies the user there.
/// </summary>
public interface IChannelNotificationService
{
    /// <summary>
    /// Channel identifier the agent names when it notifies the user.
    /// </summary>
    string Channel { get; }

    /// <summary>
    /// Hands off the current agent session to this channel, creating a new
    /// conversation thread seeded with the serialized session.
    /// </summary>
    Task HandoffAsync(string userId, string agentId, JsonElement sessionElement, string message, CancellationToken ct = default);
}
