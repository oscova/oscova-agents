namespace Oscova.Agents.Core.ApiContracts.Common;

/// <summary>
/// Reserved labels that record readers return in place of a name; the UI and CLI localize them.
/// </summary>
public static class RecordLabelKeys
{
    public const string DeletedAgent = "oscova:deleted-agent";
    public const string DeletedProvider = "oscova:deleted-provider";
    public const string DeletedMcpServer = "oscova:deleted-mcp-server";
    public const string OwnDestination = "oscova:own-destination";
    public const string OtherUsersAgents = "oscova:other-users-agents";
    public const string UsersOwnProviders = "oscova:users-own-providers";
    public const string UsersOwnModels = "oscova:users-own-models";
    public const string UsersOwnTools = "oscova:users-own-tools";
}
