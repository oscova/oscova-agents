using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.ApiContracts.Nodes;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;

namespace Oscova.Agents.Core.Projects;

/// <summary>Sends owned project operations to their node and stores output in the current central user scope.</summary>
public sealed class NodeProjectExecutor(INodeDispatcher nodes, IToolOutputStore output, NetworkSettings network, IUserPermissionService permissionService)
{
    /// <summary>Invokes one operation using the trusted run and project selected by the server.</summary>
    public async Task<JsonElement> InvokeAsync(string userId, ProjectDefinition project, string operation,
        object arguments, IReadOnlyList<string> permissions, RunContext? run, CancellationToken ct,
        NodeCallbackHandler? callback = null)
    {
        if (project.NodeId is null) { throw new InvalidOperationException("The project has no execution node."); }

        if (run is not null)
        {
            if (run.ExecutionDevice is { } device && !string.Equals(device, project.NodeId, StringComparison.Ordinal))
            {
                throw new ToolFailureException("The project's execution device changed. Start a new run for the selected device.");
            }
            // Direct skill dispatch uses this same current-operator check as routed shell calls.
            await ConversationExecutionAccess.RequireAsync(permissionService, userId, run.PermissionPreset, ct).ConfigureAwait(false);
        }

        var executionId = run?.ExecutionId ?? Guid.NewGuid().ToString("N");
        var context = new ProjectNodeContext(project.Id, project.RootPath, run?.CurrentAgentId,
            run?.ExecutionProviderId ?? null, run?.AirGapped ?? false, network.AllowSandboxNetworkAccess, run?.PermissionPreset == ChatPermissionPreset.FullAccess,
            run?.ConfiguredExecutionProviderId);
        var reply = await nodes.InvokeAsync(userId, project.NodeId, executionId, ProjectCapability.Id, ProjectCapability.Version, operation,
            JsonSerializer.SerializeToElement(context), JsonSerializer.SerializeToElement(arguments), permissions, ct, callback)
            .ConfigureAwait(false);
        var result = StoreReply(reply);
        if (reply.FilePath is { } path && FunctionInvokingChatClient.CurrentContext?.CallContent is { } call && call.Name == operation)
        {
            // The executing device resolves the address using its own filesystem and grants.
            call.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            call.AdditionalProperties[ProjectToolRouter.FilePathMetadataKey] = path;
        }

        return result;
    }

    /// <summary>Stores bounded device output in the owning user scope and rewrites its opaque references.</summary>
    public JsonElement StoreReply(NodeReply reply)
    {
        if (reply.Failure is { } failure) { throw new ToolFailureException(failure); }

        var markers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in reply.Outputs)
        {
            if (!item.Marker.StartsWith("oscova-node-output:", StringComparison.Ordinal) ||
                !Guid.TryParseExact(item.Marker["oscova-node-output:".Length..], "N", out _) ||
                !markers.Add(item.Marker))
            {
                throw new InvalidDataException("The node returned an invalid output reference.");
            }
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in reply.Outputs)
        {
            // Node-supplied labels never become central output filenames.
            replacements.Add(item.Marker, output.Save("node", item.Content)
                ?? throw new IOException("The node output could not be stored in the server workspace."));
        }

        var result = JsonNode.Parse((reply.Result ?? JsonSerializer.SerializeToElement<object?>(null)).GetRawText());
        return JsonSerializer.SerializeToElement(Rewrite(result));

        JsonNode? Rewrite(JsonNode? node)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                foreach (var replacement in replacements)
                {
                    text = text.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
                }

                return JsonValue.Create(text);
            }

            if (node is JsonObject obj)
            {
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    var child = obj[key];
                    var rewritten = Rewrite(child);
                    if (!ReferenceEquals(child, rewritten)) { obj[key] = rewritten; }
                }
            }
            else if (node is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    var child = array[index];
                    var rewritten = Rewrite(child);
                    if (!ReferenceEquals(child, rewritten)) { array[index] = rewritten; }
                }
            }

            return node;
        }
    }
}
