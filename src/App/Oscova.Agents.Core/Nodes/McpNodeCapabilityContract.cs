using Oscova.Agents.Core.ApiContracts.Nodes;
using System.Text.Json;

namespace Oscova.Agents.Core.Nodes;

/// <summary>Names the transport operations used to keep the MCP client on the central server.</summary>
public static class McpNodeCapabilityContract
{
    public const string Id = "mcp";
    public const int Version = 1;
    public const string Open = "open";
    public const string Send = "send";
    public const string Close = "close";
    public const string Ready = "ready";
    public const string Receive = "receive";

    public static NodeCapability Description { get; } = new(Id, Version,
    [
        new(Open, "Opens an authorized project MCP transport until it closes.", Schema),
        new(Send, "Sends one protocol message to the admitted transport.", Schema),
        new(Close, "Closes the admitted transport and waits for process cleanup.", Schema)
    ]);

    private static JsonElement Schema => JsonSerializer.SerializeToElement(new { type = "object" });
}
