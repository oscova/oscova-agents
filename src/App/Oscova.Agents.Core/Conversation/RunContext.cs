using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Policies;
using Oscova.Agents.Providers;
using Oscova.Agents.Tasks;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Carries the current live run metadata that app-owned tools and infrastructure can read during execution.
/// </summary>
public sealed class RunContext
{
    public required string UserId { get; init; }

    public required RunExecutionKind ExecutionKind { get; init; }

    public required string ExecutionId { get; init; }

    /// <summary>Identifies the messaging channel selected by the host.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Keeps configuration revocation attached to this run and its delegated agents.</summary>
    public Oscova.Agents.Core.Auth.ExecutionPermissionRegistry.ConfigurationScope? ConfigurationScope { get; init; }

    public ElicitationContext? Elicitation { get; init; }

    /// <summary>Gets the owned project selected by the persisted parent thread.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the root conversation's approval route for temporary delegated runs.</summary>
    public ToolApprovalContext? ToolApproval { get; init; }

    /// <summary>Routes explicit shared-chat consultations through the owning web run.</summary>
    public ChatConsultation? Consultation { get; init; }

    /// <summary>Gets the ancestor identities whose shared directory grants this invocation inherited.</summary>
    public IReadOnlyList<string> DelegationAgentIds { get; init; } = [];

    public AgentTaskContext? Task { get; init; }

    /// <summary>When true, the conversation is incognito and must not change saved memory.</summary>
    public bool IsIncognito { get; init; }

    /// <summary>
    /// When true, the run may only reach loopback or local-network endpoints.
    /// </summary>
    public bool AirGapped { get; init; }

    /// <summary>
    /// Directories that are standard for this specific run and should be merged with each agent's configured access.
    /// </summary>
    public IReadOnlyList<AllowedDirectory> StandardRunDirectories { get; init; } = [];

    /// <summary>
    /// Final resolved access policy used by tools, MCP, and script execution.
    /// </summary>
    public IReadOnlyList<AllowedDirectory> AllowedDirectories { get; init; } = [];

    public ToolApprovalRunMode ToolApprovalRunMode { get; init; } =
        ToolApprovalRunMode.UseAgentPolicy;

    /// <summary>Keeps the authenticated conversation choice fixed for this admitted run.</summary>
    public ChatPermissionPreset PermissionPreset { get; init; }

    /// <summary>Retains configured settings separately from the authorized Host override.</summary>
    public string? ConfiguredExecutionProviderId { get; init; }

    /// <summary>Names the selected execution machine for grants and approval history.</summary>
    public string? ExecutionDevice { get; init; }

    /// <summary>The execution environment captured when this run was prepared.</summary>
    public string? ExecutionProviderId { get; init; }

    /// <summary>
    /// Per-turn ledger that collects each agent run's provider egress.
    /// </summary>
    public EgressLedger? EgressLedger { get; init; }

    /// <summary>
    /// Per-turn ledger that accumulates model token usage.
    /// </summary>
    public UsageLedger? UsageLedger { get; init; }

    /// <summary>Keeps one admitted indexing task eligible while it processes multiple conversations.</summary>
    public Guid? AdmittedEmbeddingProviderId { get; set; }

    /// <summary>
    /// Per-turn ledger that accumulates tool invocation outcomes.
    /// </summary>
    public ToolInvocationLedger? ToolInvocationLedger { get; init; }

    /// <summary>
    /// Per-turn signal flipped when a fallback provider serves a response.
    /// </summary>
    public FallbackActivationSignal? FallbackSignal { get; init; }

    /// <summary>
    /// Per-turn signal flipped when automatic compaction summarizes earlier history.
    /// </summary>
    public HistorySummarizedSignal? HistorySummarizedSignal { get; init; }

    /// <summary>
    /// Display name of the currently executing agent, used to attribute fallback egress.
    /// </summary>
    public string? CurrentAgentName { get; init; }

    /// <summary>
    /// Id of the currently executing agent, used for run-scoped authorization and attribution.
    /// </summary>
    public string? CurrentAgentId { get; init; }

    /// <summary>Identifies the direct web agent allowed to post conversational questions.</summary>
    public string? QuestionAgentId { get; init; }

    /// <summary>Subagent workers owned by the root invocation. Null where the run cannot start workers.</summary>
    public DelegatedWorkState? DelegatedWork { get; init; }

    /// <summary>
    /// Returns a copy with selected run-scoped fields overridden while preserving shared run state.
    /// </summary>
    public RunContext With(
        bool? airGapped = null,
        Oscova.Agents.Core.Auth.ExecutionPermissionRegistry.ConfigurationScope? configurationScope = null,
        string? currentAgentName = null,
        string? currentAgentId = null,
        IReadOnlyList<AllowedDirectory>? allowedDirectories = null,
        IReadOnlyList<string>? delegationAgentIds = null,
        DelegatedWorkState? delegatedWork = null) => new()
    {
        UserId = UserId,
        ConfigurationScope = configurationScope ?? ConfigurationScope,
        ExecutionKind = ExecutionKind,
        ExecutionId = ExecutionId,
        ChannelId = ChannelId,
        Elicitation = Elicitation,
        ProjectId = ProjectId,
        ToolApproval = ToolApproval,
        Consultation = Consultation,
        DelegationAgentIds = delegationAgentIds ?? DelegationAgentIds,
        Task = Task,
        IsIncognito = IsIncognito,
        AirGapped = airGapped ?? AirGapped,
        StandardRunDirectories = StandardRunDirectories,
        AllowedDirectories = allowedDirectories ?? AllowedDirectories,
        ToolApprovalRunMode = ToolApprovalRunMode,
        ExecutionProviderId = ExecutionProviderId,
        ConfiguredExecutionProviderId = ConfiguredExecutionProviderId,
        PermissionPreset = PermissionPreset,
        ExecutionDevice = ExecutionDevice,
        EgressLedger = EgressLedger,
        UsageLedger = UsageLedger,
        AdmittedEmbeddingProviderId = AdmittedEmbeddingProviderId,
        ToolInvocationLedger = ToolInvocationLedger,
        FallbackSignal = FallbackSignal,
        HistorySummarizedSignal = HistorySummarizedSignal,
        CurrentAgentName = currentAgentName ?? CurrentAgentName,
        QuestionAgentId = QuestionAgentId,
        DelegatedWork = delegatedWork ?? DelegatedWork,
        CurrentAgentId = currentAgentId ?? CurrentAgentId
    };
}
