using System.Text.Json;

namespace Oscova.Agents.Core.Nodes;

/// <summary>Routes a capability's reverse request through the server invocation that authorized it.</summary>
public delegate Task<JsonElement> NodeCallbackHandler(string operation, JsonElement arguments, CancellationToken ct);
