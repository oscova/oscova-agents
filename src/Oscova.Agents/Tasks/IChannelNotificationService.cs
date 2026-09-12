using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Tasks;

/// <summary>
/// Routes task notifications and session handoffs to a specific channel.
/// </summary>
public interface IChannelNotificationService
{
    /// <summary>
    /// Channel identifier matched against <see cref="AgentTaskDefinition.NotificationChannel"/>.
    /// </summary>
    string Channel { get; }

    /// <summary>
    /// Sends a fire-and-forget notification message to the user.
    /// </summary>
    Task NotifyAsync(string userId, string agentId, string message, CancellationToken ct = default);

    /// <summary>
    /// Hands off the current agent session to this channel, creating a new
    /// conversation thread seeded with the serialized session.
    /// </summary>
    Task HandoffAsync(string userId, string agentId, JsonElement sessionElement, string message, CancellationToken ct = default);
}