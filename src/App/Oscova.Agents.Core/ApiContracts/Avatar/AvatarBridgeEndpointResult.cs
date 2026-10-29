using System.Text.Json;

namespace Oscova.Agents.Core.ApiContracts.Avatar;

/// <summary>Preserves an Avatar endpoint result for product transport adapters.</summary>
public sealed record AvatarBridgeEndpointResult(bool IsError, JsonElement Payload)
{
    /// <summary>Creates the shared failure payload used by Avatar transport adapters.</summary>
    public static AvatarBridgeEndpointResult Failure(string code, string description) =>
        new(true, JsonSerializer.SerializeToElement(new { success = false, errorCode = code, description }));
}
