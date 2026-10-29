namespace Oscova.Agents.Host.Server.Hubs;

internal static class HubGroupNames
{
    public const string Admins = "role:admin";

    public static string ForUser(string userId) => $"user:{userId}";
}
