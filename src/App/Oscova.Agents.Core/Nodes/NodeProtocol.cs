using System.Text.Json;

namespace Oscova.Agents.Core.Nodes;

/// <summary>Names the connection protocol shared by the server and execution devices.</summary>
public static class NodeProtocol
{
    public const int Version = 3;
    public const string Callback = "Callback";

    public const string HubPath = "/hubs/nodes";
    public const string Register = "Register";
    public const string ActiveConnectionError = "This node already has an active connection. Stop its other process before reconnecting.";
    public const string Ready = "Ready";
    public const string Admit = "Admit";
    public const string Invoke = "Invoke";
    public const string Cancel = "Cancel";
    public const string CleanupExecution = "CleanupExecution";
    public const string StopExecutionProvider = "StopExecutionProvider";
    public const string PolicyChanged = "PolicyChanged";
    public const int MaxPayloadBytes = 8 * 1024 * 1024;
    public const int MaxTransportMessageBytes = MaxPayloadBytes + 64 * 1024;

    /// <summary>Rejects oversized calls and results before transport admission.</summary>
    public static void ValidateSize<T>(T value)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value).Length > MaxPayloadBytes)
        {
            throw new ArgumentException($"Node payload exceeds {MaxPayloadBytes} serialized bytes. Use a smaller operation.");
        }
    }
}
