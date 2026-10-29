using Oscova.Agents.Core.Conversation;

namespace Oscova.Agents.Core.Interfaces;

public interface IUserInstructionVariablesProvider
{
    Task<UserInstructionVariables> GetVariablesAsync(string userId, CancellationToken ct = default);
}
