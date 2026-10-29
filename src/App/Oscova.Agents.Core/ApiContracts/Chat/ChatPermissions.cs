namespace Oscova.Agents.Core.ApiContracts.Chat;

/// <summary>Selects the product-owned approval and execution preset for a conversation.</summary>
public enum ChatPermissionPreset { UseAgentPolicy, AutoApprove, AutoReject, FullAccess }

/// <summary>Chooses the scope of one pending web tool decision.</summary>
public enum ToolApprovalDecision { AllowOnce, AllowForSession, Deny }

/// <summary>Changes only the saved preset of an owned conversation.</summary>
public sealed record UpdateChatPermissionsRequest(ChatPermissionPreset Preset);

/// <summary>Describes one current server-session grant without exposing tool arguments.</summary>
public sealed record SessionToolPermission(string Id, string ToolName, string AgentId, string? AgentName,
    string? ProjectId, string? ExecutionDevice, string? ExecutionTarget);

/// <summary>Returns the authoritative next-run preset and current temporary grants.</summary>
public sealed record ChatPermissions(ChatPermissionPreset Preset, bool CanUseFullAccess, string ExecutionMachine,
    IReadOnlyList<SessionToolPermission> Grants, ChatPermissionPreset? RunningPreset = null);
