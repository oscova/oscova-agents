using Oscova.Agents.Core.ApiContracts.Agents;

using Oscova.Agents.Secrets;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Oscova.Agents.Collections;
using Oscova.Agents.Browsing;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Harness;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Execution;
using Oscova.Agents.Skills;
using Oscova.Agents.Tasks;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Definitions;
using Oscova.Agents.Llm;
using Oscova.Agents.Memory;
using Oscova.Agents.FileBackups;
using Oscova.Agents.Providers;
using Oscova.Agents.Runtime;
using Oscova.Agents.Utils;
using Oscova.Agents.Workspace;
using OscovaSummarizingChatReducer = Oscova.Agents.Reducers.SummarizingChatReducer;

#pragma warning disable MAAI001

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Adapts host-owned user context, skills, and memory services into the framework agent orchestrator.
/// </summary>
public class AgentCompositionService(
    IUserDirectoryService userDirectoryService,
    IUserInstructionVariablesProvider userInstructionVariablesProvider,
    IAgentMemoryService agentMemoryService,
    ExecutionSettings executionSettings,
    PrivacySettings privacySettings,
    ISharedAgentPersonalContextRepository sharedAgentContextRepository,
    RunContextAccessor runContextAccessor,
    IAgentOrchestrator agentOrchestrator,
    IEnumerable<ToolsetRegistration> toolsets,
    IAgentDefinitionRepository agentDefinitions,
    ProjectAccessService projectAccess,
    ToolApprovalRunLoop approvalLoop,
    IContentProtector contentProtector,
    IUserFileBackupService fileBackups,
    ProjectToolRouter projectTools,
    NodeProjectExecutor nodeProjects,
    IUserPermissionService permissions,
    AgentFileSkillScriptRunner? skillScriptRunner = null,
    IReadOnlyList<AITool>? memoryTools = null,
    IReadOnlyList<AITool>? libraryTools = null)
{
    /// <summary>Returns the managed memory tool identities available to task scope validation.</summary>
    public IReadOnlyList<string> MemoryToolNames => memoryTools?.Select(tool => tool.Name).ToArray() ?? [];

    /// <summary>Gets or sets the memory tools that change saved memory, withheld from incognito runs.</summary>
    public IReadOnlySet<string> MemoryWriteToolNames { get; set; } = new HashSet<string>();

    private const string AgentPrivateLabel = UserFileNames.AgentPrivateDirectory;
    private const string SharedFolderLabel = "shared";

    public AgentToolCollection Tools => agentOrchestrator.Tools;

    // Microsoft.Agents.AI exposes no constants for its todo tool names.
    private static readonly string[] TodoToolNames = ["todos_add", "todos_complete", "todos_get_all", "todos_get_remaining", "todos_remove"];

    /// <summary>
    /// Returns every built-in and plugin tool name, including the tools context providers add per run.
    /// MCP tools and agents called as tools are left out, since their names come from user-chosen names.
    /// </summary>
    public IReadOnlySet<string> GetBuiltInToolNames()
    {
        var tools = Tools;
        return tools.Where(tool => tools.GetExternalToolMetadata(tool.Name) is null)
            .Select(tool => tool.Name)
            .Concat(MemoryToolNames)
            .Concat(libraryTools?.Select(tool => tool.Name) ?? [])
            .Concat(TodoToolNames)
            .Concat([
                ToolSearchProvider.SearchToolName,
                AgentAsToolProvider.StartToolName, AgentAsToolProvider.StatusToolName, AgentAsToolProvider.MessageToolName,
                AgentAsToolProvider.InterruptToolName, AgentAsToolProvider.ContinueToolName,
                AgentSkillsProvider.LoadSkillToolName, AgentSkillsProvider.ReadSkillResourceToolName, AgentSkillsProvider.RunSkillScriptToolName
            ])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets or sets the browser binding shared with this user's workspace.</summary>
    public BrowserWorkspaceService? BrowserWorkspace { get; set; }

    /// <summary>Gets or sets the current instruction defaults bound to this user's active contributions.</summary>
    public IReadOnlyDictionary<string, string> InstructionDefaults { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Returns a managed agent's retained default or its bound host persona.</summary>
    public string? GetAgentInstructionDefault(AgentDefinition definition)
        => definition.InstructionSource is null ? null
            : definition.DefaultInstruction ?? InstructionDefaults.GetValueOrDefault(definition.InstructionSource);

    private IReadOnlyList<string> BrowserToolNames => toolsets.Where(registration => registration.Toolset is IBrowserCapability)
        .SelectMany(registration => ToolNaming.GetToolsetQualifiedNames(registration.Toolset.GetType(), registration.Origin)).ToArray();

    private IReadOnlyList<string> ShellToolNames => toolsets.Where(registration => ProjectToolRouter.IsShellToolset(registration.Toolset.GetType()))
        .SelectMany(registration => ToolNaming.GetToolsetQualifiedNames(registration.Toolset.GetType(), registration.Origin)).ToArray();

    /// <summary>Resolves account-owned external tools for the current execution environment.</summary>
    public Func<AgentToolCollection, RunContext?, CancellationToken, IReadOnlyCollection<string>?, Task<AgentToolCollection?>>? ResolveRuntimeToolsAsync { get; set; }

    public AIAgentCollection AIAgents => agentOrchestrator.AIAgents;

    /// <summary>Allows the host to attach account policy to the framework's existing skill source.</summary>
    public Func<AgentSkillsSource, AgentSkillsSource>? DecorateSkills { get; set; }
    public IExecutionEnvironmentCatalog? ExecutionEnvironments { get; set; }
    public IReadOnlyList<string> NotificationChannels { get; set; } = [];
    private string? ExecutionProviderId => runContextAccessor.Current is { } run ? run.ExecutionProviderId : executionSettings.ProviderId;

    /// <summary>Rebuilds tools with their execution routing and original governance metadata intact.</summary>
    public void SyncTools(IReadOnlyList<AIFunction> mcpTools,
        IReadOnlyDictionary<string, ExternalToolMetadata> mcpMetadata)
    {
        IEnumerable<ToolsetRegistration> availableToolsets =
            ExecutionEnvironments?.Find(ExecutionProviderId)?.AllowsHostCommand == true
                ? toolsets
                : toolsets.Where(registration => registration.Toolset is not HostShellToolset);
        agentOrchestrator.SyncTools(availableToolsets, mcpTools, mcpMetadata, projectTools.Decorate);
    }

    private ToolGovernanceSettings? _toolGovernance;

    /// <summary>Applies per-user governance metadata to the selected tool collection.</summary>
    public void ApplyToolGovernance(ToolGovernanceSettings settings, AgentToolCollection? collection = null)
    {
        _toolGovernance = settings;
        var tools = collection ?? Tools;
        foreach (var tool in tools)
        {
            var networkScope = settings.Find(tool.Name)?.NetworkScopeOverride
                ?? tools.GetDeclaredNetworkScope(tool.Name);
            tools.TrySetNetworkScope(tool.Name, networkScope);
        }
    }

    /// <summary>
    /// Removes a single cached agent so it will be rebuilt on next access.
    /// </summary>
    public void EvictAgent(string agentId) => agentOrchestrator.EvictAgent(agentId);

    /// <summary>
    /// Removes all cached agents while preserving the current tool collection.
    /// </summary>
    public void InvalidateAllAgents() => agentOrchestrator.InvalidateAllAgents();

    /// <summary>
    /// Discards all cached tools and agents, forcing a full rebuild on next access.
    /// </summary>
    public void InvalidateAll() => agentOrchestrator.InvalidateAll();

    /// <summary>
    /// Returns a cached agent or builds a new one with the full context-provider pipeline.
    /// </summary>
    public async Task<ChatClientAgent> GetOrCreateAgentAsync(
        string userId,
        AgentDefinition agentDefinition,
        IReadOnlyList<AgentDefinition> availableAgentDefinitions,
        Func<AgentDefinition, CancellationToken, Task<IChatClient>> chatClientFactory,
        Func<ChatClientBuilder, ChatClientBuilder>? configureBuilder = null,
        Action<ChatClientAgentOptions>? configureOptions = null,
        IEnumerable<AIContextProvider>? extraProviders = null,
        bool bypassRequestedAgentCache = false,
        Func<AgentDefinition, ModelProviderConfig?>? modelProviderResolver = null,
        TaskRunSurface? taskRunSurface = null,
        Func<AgentDefinition, ChatClientBuilder, ChatClientBuilder>? configureAgentBuilder = null,
        Func<AgentDefinition, ModelProviderConfig?>? chatReducerProviderResolver = null)
    {
        IReadOnlyList<AllowedDirectory>? activeRunDirectories = taskRunSurface?.Directories;
        // Context providers and tool exposure must inherit the initiating agent's offline posture.
        bool activeRunAirGapped = privacySettings.IsLocalOnly || agentDefinition.IsLocalOnly || runContextAccessor.Current?.AirGapped == true;
        IReadOnlyList<AllowedDirectory> ResolveDirectories(AgentDefinition definition)
        {
            if (runContextAccessor.Current is { DelegationAgentIds.Count: > 0 } invocation &&
                string.Equals(invocation.CurrentAgentId, definition.Id, StringComparison.Ordinal))
            {
                return invocation.AllowedDirectories;
            }

            return activeRunDirectories is not null &&
                   string.Equals(definition.Id, agentDefinition.Id, StringComparison.Ordinal)
                ? activeRunDirectories
                : ResolveAllowedDirectories(userId, definition);
        }

        var compatibleAgentDefinitions = availableAgentDefinitions
            .Prepend(agentDefinition)
            .DistinctBy(candidate => candidate.Id)
            .ToList();

        var recoveryInstructions = await ReadPromptInstructionAsync(userId, PromptFileNames.ToolFailureRecoveryPrompt)
            .ConfigureAwait(false) ?? ToolFailureMiddleware.DefaultRecoveryInstructions;

        // Exposed children share this tool snapshot, unrelated available agents do not require its connections.
        var requiredAgents = new List<AgentDefinition> { agentDefinition };
        for (var index = 0; index < requiredAgents.Count; index++)
        {
            foreach (var child in compatibleAgentDefinitions.Where(candidate => candidate.IsExposedAsTool &&
                         requiredAgents[index].AgentToolIds.Contains(candidate.Id)))
            {
                if (!requiredAgents.Contains(child)) { requiredAgents.Add(child); }
            }
        }

        var requiredServers = requiredAgents.SelectMany(agent => agent.AllowedToolNames.Concat(agent.PinnedToolNames))
            .Select(name => ToolNaming.SplitQualifiedName(name).Qualifier).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var runtimeTools = ResolveRuntimeToolsAsync is null ? null :
            await ResolveRuntimeToolsAsync(agentOrchestrator.Tools, runContextAccessor.Current,
                runContextAccessor.Current?.ConfigurationScope?.Token ?? CancellationToken.None,
                requiredServers).ConfigureAwait(false);
        if (runtimeTools is not null && _toolGovernance is not null) { ApplyToolGovernance(_toolGovernance, runtimeTools); }

        Func<CancellationToken, Task<ChatClientAgent>>? createSelfWorker = null;
        var request = new AgentBuildRequest(
            chatClientFactory,
            agentDefinition,
            compatibleAgentDefinitions,
            async (definition, cancellationToken) =>
                await CreateProvidersAsync(
                    userId,
                    definition,
                    agentDefinition.Id,
                    activeRunAirGapped,
                    extraProviders,
                    cancellationToken,
                    activeRunDirectories,
                    compatibleAgentDefinitions, runtimeTools, createSelfWorker).ConfigureAwait(false),
            ResolveDirectories,
            configureBuilder,
            configureOptions,
            bypassRequestedAgentCache || taskRunSurface?.IsScopeApplied == true,
            async (definition, chatClient, cancellationToken) => await CreateChatReducerAsync(
                    userId,
                    definition,
                    chatClient,
                    (chatReducerProviderResolver ?? modelProviderResolver)?.Invoke(definition),
                    cancellationToken)
                .ConfigureAwait(false),
            runtimeTools is null ? configureAgentBuilder : (definition, builder) => configureAgentBuilder?.Invoke(definition, builder) ?? builder,
            builder => builder.UseToolFailureReporting(recoveryInstructions),
            definition => modelProviderResolver?.Invoke(definition)?.ChatOptions);

        if (agentDefinition.IsSubagentDelegationEnabled)
        {
            // A worker keeps the parent's identity, model, tools, image analysis and tool hooks, with fresh history.
            // It drops run observers, parent-bound extra providers and delegation, and skips rebuilding exposed agents.
            var workerDefinition = JsonSerializer.Deserialize<AgentDefinition>(JsonSerializer.Serialize(agentDefinition))!;
            workerDefinition.AgentToolIds.Clear();
            var workerRequest = request with
            {
                AgentDefinition = workerDefinition,
                ContextProviderFactory = async (definition, cancellationToken) =>
                    await CreateProvidersAsync(
                        userId,
                        definition,
                        agentDefinition.Id,
                        activeRunAirGapped,
                        extraProviders?.OfType<VisionToolProvider>().ToList(),
                        cancellationToken,
                        activeRunDirectories,
                        compatibleAgentDefinitions, runtimeTools, worker: true).ConfigureAwait(false),
                ConfigureBuilder = null,
                ConfigureOptions = null,
                BypassRequestedAgentCache = true
            };
            createSelfWorker = cancellationToken => agentOrchestrator.GetOrCreateAgentAsync(workerRequest, cancellationToken);
        }

        return await agentOrchestrator.GetOrCreateAgentAsync(request).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders the instructions the active agent would receive from its context providers.
    /// </summary>
    public async Task<string> PreviewInstructionsAsync(
        string userId,
        AgentDefinition agentDefinition,
        CancellationToken cancellationToken = default)
    {
        var preview = await PreviewContextAsync(
            userId,
            agentDefinition,
            messages: null,
            extraProviders: null,
            invokingAgent: null,
            previewSession: null,
            cancellationToken).ConfigureAwait(false);

        return preview.Instructions;
    }

    /// <summary>
    /// Renders the context providers the active agent would receive without issuing a model request.
    /// </summary>
    public async Task<AgentContextPreview> PreviewContextAsync(
        string userId,
        AgentDefinition agentDefinition,
        IEnumerable<ChatMessage>? messages,
        IEnumerable<AIContextProvider>? extraProviders,
        AIAgent? invokingAgent,
        AgentSession? previewSession,
        CancellationToken cancellationToken = default)
    {
        // Preview declares the runtime subagent tools and guidance, but never starts a worker.
        await using var previewWork = new DelegatedWorkState(CancellationToken.None);
        var providers = await CreateProvidersAsync(
            userId,
            agentDefinition,
            agentDefinition.Id,
            privacySettings.IsLocalOnly || agentDefinition.IsLocalOnly || runContextAccessor.Current?.AirGapped == true,
            extraProviders,
            cancellationToken,
            createSelfWorker: agentDefinition.IsSubagentDelegationEnabled
                ? static _ => Task.FromException<ChatClientAgent>(new InvalidOperationException("Instruction preview never starts subagents."))
                : null,
            resolveWork: () => previewWork).ConfigureAwait(false);

        return await AgentInstructionPreviewRenderer.RenderContextAsync(
            providers,
            messages,
            invokingAgent,
            previewSession,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the reusable local input estimate for an unsaved agent configuration.</summary>
    public async Task<AgentInstructionPreview> PreviewUsageAsync(
        string userId,
        AgentDefinition definition,
        IEnumerable<AIContextProvider>? extraProviders = null,
        AIAgent? invokingAgent = null,
        CancellationToken cancellationToken = default)
    {
        // A fresh provider pass excludes tools unlocked by an existing conversation.
        var preview = await PreviewContextAsync(
            userId, definition, messages: null, extraProviders, invokingAgent,
            previewSession: null, cancellationToken).ConfigureAwait(false);
        var tools = new List<AgentContextTool>();
        foreach (var contribution in preview.ToolContributions)
        {
            AddTools(contribution.InitialTools, contribution.Source, isDeferred: false);
            AddTools(contribution.DeferredTools, contribution.Source, isDeferred: true);
        }

        var instructionTokens = ContextWindowTokenEstimator.EstimateText(preview.Instructions);
        var messageTokens = ContextWindowTokenEstimator.EstimateMessages(preview.Messages);
        var toolTokens = ContextWindowTokenEstimator.EstimateTools(preview.Tools);
        return new AgentInstructionPreview
        {
            AgentId = definition.Id,
            Instructions = preview.Instructions,
            InstructionTokens = instructionTokens,
            MessageTokens = messageTokens,
            ToolTokens = toolTokens,
            EstimatedInputTokens = instructionTokens + messageTokens + toolTokens,
            ToolAssemblyAdjustmentTokens = toolTokens - tools.Where(tool => !tool.IsDeferred).Sum(tool => tool.EstimatedTokens),
            Contributions = preview.Contributions,
            Tools = tools,
            GeneratedAt = DateTimeOffset.UtcNow
        };

        void AddTools(IEnumerable<AITool> declarations, string source, bool isDeferred)
        {
            foreach (var tool in declarations)
            {
                tools.Add(new AgentContextTool
                {
                    Source = source,
                    ServerName = Tools.GetServerName(tool.Name),
                    Name = tool.Name,
                    Description = tool.Description ?? string.Empty,
                    JsonSchema = tool is AIFunctionDeclaration function ? function.JsonSchema.ToString() : string.Empty,
                    EstimatedTokens = ContextWindowTokenEstimator.EstimateTools([tool]),
                    IsDeferred = isDeferred
                });
            }
        }
    }

    private async Task<IReadOnlyList<AIContextProvider>> CreateProvidersAsync(
            string userId,
            AgentDefinition definition,
            string activeAgentId,
            bool activeRunAirGapped,
            IEnumerable<AIContextProvider>? extraProviders,
            CancellationToken cancellationToken,
            IReadOnlyList<AllowedDirectory>? activeRunDirectories = null,
            IReadOnlyList<AgentDefinition>? buildDefinitions = null,
            AgentToolCollection? runtimeTools = null,
            Func<CancellationToken, Task<ChatClientAgent>>? createSelfWorker = null,
            bool worker = false,
            Func<DelegatedWorkState?>? resolveWork = null)
        {
            resolveWork ??= () => runContextAccessor.Current?.DelegatedWork;
            var isActiveAgent = string.Equals(definition.Id, activeAgentId, StringComparison.Ordinal);
            var hasWebApprovalRoute = runContextAccessor.Current?.ExecutionKind == RunExecutionKind.WebChat &&
                buildDefinitions is not null;
            // A non-web worker has no approval route, so it keeps the restrictions of a delegated agent.
            bool suppressApprovalTools = (!isActiveAgent || worker) && !hasWebApprovalRoute;
            Func<IReadOnlyList<AllowedDirectory>> workspaceResolver = () =>
                runContextAccessor.Current is { DelegationAgentIds.Count: > 0 } invocation &&
                string.Equals(invocation.CurrentAgentId, definition.Id, StringComparison.Ordinal)
                    ? invocation.AllowedDirectories
                    : isActiveAgent && activeRunDirectories is not null
                        ? activeRunDirectories
                        : ResolveAllowedDirectories(userId, definition);
            var airGapped = activeRunAirGapped || definition.IsLocalOnly;
            var toolResolver = new AgentToolResolver(
                runtimeTools ?? agentOrchestrator.Tools,
                definition.ToolPolicies,
                airGapped,
                suppressApprovalTools);
            var toolSearchInstructions = await ReadPromptInstructionAsync(userId, PromptFileNames.ToolSearchPrompt).ConfigureAwait(false);
            // Offer shell tools only when the account can run them, instead of failing at execution.
            var shellGranted = string.IsNullOrEmpty(userId) || await projectTools.IsShellGrantedAsync(
                userId, runContextAccessor.Current?.ProjectId is not null, cancellationToken).ConfigureAwait(false);
            // Only the active agent offers same-agent subagents. Workers get no delegation provider.
            createSelfWorker = isActiveAgent ? createSelfWorker : null;
            var delegates = !worker && (definition.AgentToolIds.Count > 0 || createSelfWorker is not null);
            var providers = new List<AIContextProvider>
            {
                await CreateEnvironmentProviderAsync(userId, workspaceResolver).ConfigureAwait(false),
                BuildToolExposureProvider(definition, toolResolver, airGapped, toolSearchInstructions, suppressApprovalTools, runtimeTools, shellGranted, worker, delegates),
                CreateInstructionProvider(userId, definition)
            };

            if (!airGapped && BrowserWorkspace is { IsAvailable: true } browser &&
                definition.AllowedToolNames.Intersect(BrowserToolNames, StringComparer.OrdinalIgnoreCase).Any(name => toolResolver.Resolve(name) is not null))
            {
                providers.Add(new BrowserContextProvider(browser,
                    await ReadPromptInstructionAsync(userId, PromptFileNames.BrowserUsePrompt).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The browser instruction contribution is unavailable."), runContextAccessor));
            }

            if (isActiveAgent && !definition.IsShared)
            {
                providers.Add(new PrivateNotesContextProvider(userId, userDirectoryService, contentProtector, definition, fileBackups));
            }

            if (delegates)
            {
                var instructions = createSelfWorker is null ? null
                    : await ReadPromptInstructionAsync(userId, PromptFileNames.SubagentsPrompt).ConfigureAwait(false)
                        ?? AgentAsToolProvider.DefaultInstructions;
                providers.Add(hasWebApprovalRoute
                    ? new ApprovingAgentAsToolProvider(definition, buildDefinitions!, agentOrchestrator.AIAgents,
                        this, agentDefinitions, projectAccess, privacySettings, runContextAccessor, approvalLoop, createSelfWorker)
                    { Instructions = instructions }
                    : new AgentAsToolProvider(definition.AgentToolIds, agentOrchestrator.AIAgents,
                        resolveWork, definition.Id, createSelfWorker)
                    { Instructions = instructions });
            }

            if (isActiveAgent && definition.AllowedSkillNames.Count > 0 && !string.IsNullOrEmpty(userId))
            {
                string skillsPath = userDirectoryService.GetSkillsDirectoryPath(userId);
                var allowedSkillNames = new HashSet<string>(definition.AllowedSkillNames, StringComparer.OrdinalIgnoreCase);
                // Skill scripts need an approval, so an agent without an approval route gets none.
                var fileSkillOptions = SkillFileDiscoveryOptions.Create(
                    ExecutionProviderId != null && !suppressApprovalTools);

                var skillProviderBuilder = new AgentSkillsProviderBuilder()
                    .UseSource(logs =>
                    {
                        AgentSkillsSource source = CreateFileSkillsSource(skillsPath, fileSkillOptions, logs);
                        return DecorateSkills?.Invoke(source) ?? source;
                    })
                    .UseOptions(options =>
                    {
                        options.DisableLoadSkillApproval = true;
                        options.DisableReadSkillResourceApproval = true;
                        // The framework always offers the script tool. Without an approval route it finds no scripts,
                        // so it only reports a missing script and needs no approval.
                        options.DisableRunSkillScriptApproval = suppressApprovalTools;
                    })
                    // Agent settings persist the explicit skill names assigned to this agent.
                    .UseFilter((skill, _) => allowedSkillNames.Contains(skill.Frontmatter.Name));

                var skillsPrompt = await ReadPromptInstructionAsync(userId, PromptFileNames.SkillsPrompt).ConfigureAwait(false);
                if (skillsPrompt is not null)
                {
                    skillProviderBuilder.UsePromptTemplate(skillsPrompt);
                }

                providers.Add(skillProviderBuilder.Build());
            }

            if (isActiveAgent && definition.IsMemoryEnabled && !string.IsNullOrEmpty(userId))
            {
                var selectedMemoryTools = memoryTools?
                    .Where(tool => definition.AllowedToolNames.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                // Explicit task selections narrow the managed memory tools.
                var availableMemoryTools = selectedMemoryTools is { Count: > 0 } ? selectedMemoryTools : memoryTools;
                if (runContextAccessor.Current?.IsIncognito == true)
                {
                    // Incognito chats may recall memory but never change it.
                    availableMemoryTools = availableMemoryTools?.Where(tool => !MemoryWriteToolNames.Contains(tool.Name)).ToList();
                }

                var memoryProvider = new MemoryContextProvider(agentMemoryService, userId, memoryTools: availableMemoryTools);
                if (userDirectoryService.FileExists(userId, PromptFileNames.MemorySystemPrompt))
                {
                    memoryProvider.SystemPrompt = await ReadPromptInstructionAsync(userId, PromptFileNames.MemorySystemPrompt)
                        .ConfigureAwait(false) ?? MemoryContextProvider.DefaultMemoryPrompt;
                }

                providers.Add(memoryProvider);
            }

            if (isActiveAgent && !string.IsNullOrEmpty(userId))
            {
                var libraryRoot = userDirectoryService.GetWorkspaceDirectoryPath(
                    userId,
                    UserFileNames.LibraryWorkspaceDirectory);
                // The resolved Library grant is the single switch for its prompt and managed tools.
                var libraryDirectory = workspaceResolver().FirstOrDefault(directory =>
                    string.Equals(
                        directory.Label,
                        UserFileNames.LibraryWorkspaceDirectory,
                        StringComparison.OrdinalIgnoreCase) &&
                    directory.Kind == DirectorySourceKind.LocalPath &&
                    PathHelper.ArePathsEqual(directory.Value, libraryRoot));
                if (libraryDirectory is not null)
                {
                    var availableLibraryTools = (libraryTools ?? [])
                        .ToList();
                    var libraryProvider = new LibraryContextProvider(
                        PathHelper.ResolveToAgentPath(libraryDirectory.Value, [libraryDirectory]),
                        availableLibraryTools);
                    var libraryPrompt = await ReadPromptInstructionAsync(
                        userId,
                        PromptFileNames.LibraryPrompt).ConfigureAwait(false);
                    if (libraryPrompt is not null)
                    {
                        libraryProvider.SystemPrompt = libraryPrompt;
                    }

                    providers.Add(libraryProvider);
                }
            }

            if (isActiveAgent && definition.IsTodoEnabled)
            {
                providers.Add(new OmitEmptyTodoListProvider(new TodoProvider(new TodoProviderOptions
                {
                    Instructions = await ReadPromptInstructionAsync(userId, PromptFileNames.TodoProviderPrompt)
                        .ConfigureAwait(false)
                })));
            }

            if (isActiveAgent && extraProviders is not null)
            {
                providers.AddRange(extraProviders);
            }

            return providers;
        }

    /// <summary>Returns the framework's not-found error for a skill script that cannot run, or null to ask the user.</summary>
    public async Task<string?> FindMissingSkillScriptAsync(string userId, string skillName, string scriptName,
        CancellationToken cancellationToken)
    {
        var source = CreateFileSkillsSource(userDirectoryService.GetSkillsDirectoryPath(userId),
            SkillFileDiscoveryOptions.Create(ExecutionProviderId != null), logs: null);
        var skills = await source.GetSkillsAsync(SkillLookupContext, cancellationToken).ConfigureAwait(false);
        // Matches the framework's exact name lookup and error text in its run_skill_script tool.
        var skill = skills.FirstOrDefault(candidate => candidate.Frontmatter.Name == skillName);
        // The running agent's skill filter hides unassigned skills, so the framework reports those as not found too.
        var agentId = runContextAccessor.Current?.CurrentAgentId;
        var agent = agentId is null ? null : await agentDefinitions.GetAgentAsync(userId, agentId, cancellationToken).ConfigureAwait(false);
        if (skill is null || agent?.AllowedSkillNames.Contains(skillName, StringComparer.OrdinalIgnoreCase) == false)
        {
            return $"Error: Skill '{skillName}' not found.";
        }

        return await skill.GetScriptAsync(scriptName, cancellationToken).ConfigureAwait(false) is null
            ? $"Error: Script '{scriptName}' not found in skill '{skillName}'."
            : null;
    }

    // The file source reads only its directory, so lookups outside a run use an agent that never runs.
    private static readonly AgentSkillsSourceContext SkillLookupContext = new(new ChatClientAgent(new UnusedChatClient()), null);

    /// <summary>Satisfies the skill source's agent requirement for lookups that never call a model.</summary>
    private sealed class UnusedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private AgentFileSkillsSource CreateFileSkillsSource(string skillsPath, AgentFileSkillsSourceOptions options, ILoggerFactory? logs)
        => new(skillsPath, skillScriptRunner ?? DisabledSkillScriptRunner, options, logs);

    private static Task<object?> DisabledSkillScriptRunner(
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Skill script execution requires a configured script runner.");
    }

    /// <summary>Creates the configured conversation reducer for one user and resolved model.</summary>
    public async Task<OscovaSummarizingChatReducer> CreateChatReducerAsync(
        string userId,
        AgentDefinition _,
        IChatClient chatClient,
        ModelProviderConfig? modelProviderConfig,
        CancellationToken cancellationToken)
    {
        // Budget follows the resolved model's context window; unknown sizes keep the default.
        var budget = OscovaSummarizingChatReducer.DeriveHistoryBudget(
            modelProviderConfig?.ContextWindowSize ?? 0,
            modelProviderConfig?.ChatOptions.MaxOutputTokens);
        var reducer = new OscovaSummarizingChatReducer(chatClient, budget);
        // Reducers outlive runs on cached agents, so the signal is read from the active run at call time.
        reducer.OnHistorySummarized += () => runContextAccessor.Current?.HistorySummarizedSignal?.MarkSummarized();
        if (string.IsNullOrWhiteSpace(userId) ||
            !userDirectoryService.FileExists(userId, PromptFileNames.CompactionPrompt))
        {
            return reducer;
        }

        cancellationToken.ThrowIfCancellationRequested();
        reducer.SummarizationPrompt = await ReadPromptInstructionAsync(userId, PromptFileNames.CompactionPrompt, cancellationToken)
            .ConfigureAwait(false) ?? OscovaSummarizingChatReducer.DefaultSummarizationPrompt;

        return reducer;
    }

    private AIContextProvider BuildToolExposureProvider(
        AgentDefinition definition,
        AgentToolResolver resolver,
        bool airGapped,
        string? toolSearchInstructions,
        bool suppressApprovalRequiredTools = false,
        AgentToolCollection? runtimeTools = null,
        bool shellGranted = true,
        bool worker = false,
        bool delegates = false)
    {
        // Filter before policy resolution so a child Auto Approve override cannot restore Host Shell.
        IEnumerable<string> allowedToolNames = suppressApprovalRequiredTools
            ? definition.AllowedToolNames.Where(name => !string.Equals(
                name,
                HostShellToolset.QualifiedToolName,
                StringComparison.OrdinalIgnoreCase))
            : definition.AllowedToolNames;

        if (worker || runContextAccessor.Current?.QuestionAgentId != definition.Id)
        {
            allowedToolNames = allowedToolNames.Where(name => !string.Equals(
                name, UserQuestions.QualifiedToolName, StringComparison.OrdinalIgnoreCase));
        }

        if (airGapped || BrowserWorkspace?.IsAvailable != true || runContextAccessor.Current?.ExecutionKind != RunExecutionKind.WebChat)
        { allowedToolNames = allowedToolNames.Except(BrowserToolNames, StringComparer.OrdinalIgnoreCase); }

        if (!shellGranted)
        { allowedToolNames = allowedToolNames.Except(ShellToolNames, StringComparer.OrdinalIgnoreCase); }

        // Delegation instructions promise the wait tool, so it stays selected and visible with them.
        IEnumerable<string> pinnedToolNames = definition.PinnedToolNames;
        if (delegates)
        {
            allowedToolNames = allowedToolNames.Union([WaitToolset.QualifiedToolName], StringComparer.OrdinalIgnoreCase);
            pinnedToolNames = pinnedToolNames.Union([WaitToolset.QualifiedToolName], StringComparer.OrdinalIgnoreCase);
        }

        if (definition.IsToolSearchEnabled)
        {
            return new ToolSearchProvider(
                allowedToolNames,
                pinnedToolNames,
                runtimeTools ?? agentOrchestrator.Tools,
                resolver,
                new LexicalToolSearchStrategy(),
                toolSearchInstructions);
        }

        // Tool exposure stays separate from workspace enforcement; the path policy runs in middleware.
        return new AgentToolProvider(
            allowedToolNames,
            runtimeTools ?? agentOrchestrator.Tools,
            definition.ToolPolicies,
            airGapped,
            suppressApprovalRequiredTools);
    }

    /// <summary>Resolves a topic's mode and body before any caller renders its variables.</summary>
    public async Task<string?> ReadPromptInstructionAsync(string userId, string promptPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) { return null; }

        return await userDirectoryService.ReadPromptInstructionAsync(userId, promptPath,
            InstructionDefaults.GetValueOrDefault(promptPath), ct).ConfigureAwait(false);
    }

    private async Task<AIContextProvider> CreateEnvironmentProviderAsync(
        string userId, Func<IReadOnlyList<AllowedDirectory>> workspaceResolver)
    {
        var provider = EnvironmentContextProvider.FromWorkspaceResolver(workspaceResolver);
        RuntimeEnvironmentInfo hostEnvironment = await CreateHostRuntimeEnvironmentInfoAsync(workspaceResolver).ConfigureAwait(false);
        provider.HostEnvironment = hostEnvironment;
        provider.NotificationChannels = NotificationChannels;
        provider.RuntimeEnvironment = ExecutionEnvironments?.Find(ExecutionProviderId) is { } definition
            ? new RuntimeEnvironmentInfo(
                Os: definition.Os,
                Architecture: RuntimeInformation.OSArchitecture.ToString(),
                Shell: definition.Id == "host" ? hostEnvironment.Shell : definition.Shell,
                PathSeparator: definition.UsesHostPaths ? hostEnvironment.PathSeparator : "/",
                WorkingDirectory: definition.UsesHostPaths ? hostEnvironment.WorkingDirectory : PathHelper.ResolveWorkingDirectoryAgentPath(workspaceResolver()) ?? "not available",
                HomeDirectory: definition.HomeDirectory ?? (definition.UsesHostPaths ? hostEnvironment.HomeDirectory : "not available"),
                TempDirectory: definition.TempDirectory ?? (definition.UsesHostPaths ? hostEnvironment.TempDirectory : "not available"),
                UsesHostPaths: definition.UsesHostPaths)
            : ExecutionProviderId is null ? hostEnvironment
                : new RuntimeEnvironmentInfo("unavailable", hostEnvironment.Architecture, "unavailable", "/",
                    "not available", "not available", "not available", UsesHostPaths: false);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            provider.SystemPrompt = await ReadPromptInstructionAsync(userId, PromptFileNames.EnvironmentPrompt)
                .ConfigureAwait(false) ?? EnvironmentPromptDefaults.DefaultInstructions;
        }

        return new ProjectEnvironmentContextProvider(provider, runContextAccessor, projectAccess, nodeProjects, workspaceResolver);
    }

    private static async Task<RuntimeEnvironmentInfo> CreateHostRuntimeEnvironmentInfoAsync(
        Func<IReadOnlyList<AllowedDirectory>> workspaceResolver)
    {
        await using var shell = new LocalShellExecutor(new LocalShellExecutorOptions { Mode = ShellMode.Persistent });
        var directories = workspaceResolver();
        return new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
            shell.ResolvedShellBinary, Path.DirectorySeparatorChar.ToString(),
            PathHelper.ResolveWorkingDirectoryHostPath(directories) ?? directories.FirstOrDefault()?.Value ?? "not available",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath(), UsesHostPaths: true);
    }

    private AgentPersonalContextProvider CreateInstructionProvider(string userId, AgentDefinition agentDefinition)
    {
        return new AgentPersonalContextProvider(
            agentDefinition.InstructionSource is null ? agentDefinition.SystemInstruction
                : InstructionSelection.Select(agentDefinition.InstructionMode, agentDefinition.SystemInstruction,
                    GetAgentInstructionDefault(agentDefinition)),
            async cancellationToken =>
            {
                var variables = await userInstructionVariablesProvider.GetVariablesAsync(userId, cancellationToken).ConfigureAwait(false);
                var userContext = string.Empty;

                // File-backed personal context is only supported for shared agents.
                if (agentDefinition.IsShared)
                {
                    userContext = await sharedAgentContextRepository.GetAsync(userId, agentDefinition, cancellationToken).ConfigureAwait(false);
                }

                var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, variables.TimeZone);
                return new
                {
                    agent_name = agentDefinition.Name,
                    current_date = now.ToString("yyyy-MM-dd"),
                    current_time = now.ToString("HH:mm"),
                    user_name = variables.UserName,
                    user_nickname = variables.UserNickname,
                    user_username = variables.UserUserName,
                    user_email = variables.UserEmail,
                    user_timezone = variables.TimeZone.Id,
                    user_context = userContext
                };
            });
    }

    public IReadOnlyList<AllowedDirectory> ResolveAllowedDirectories(
        string userId,
        AgentDefinition agentDefinition,
        IReadOnlyList<AllowedDirectory>? standardRunDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return [];
        }

        var directories = new List<AllowedDirectory>();

        foreach (string workspaceName in OrderDefaultFirst(agentDefinition.AllowedWorkspaces))
        {
            string workspacePath = userDirectoryService.GetWorkspaceDirectoryPath(userId, workspaceName);
            // Library sources are user-managed through Files; agents may read but never mutate them.
            WorkspaceAccessMode accessMode = string.Equals(
                workspaceName,
                UserFileNames.LibraryWorkspaceDirectory,
                StringComparison.OrdinalIgnoreCase)
                ? WorkspaceAccessMode.ReadOnly
                : WorkspaceAccessMode.ReadWrite;
            directories.Add(new AllowedDirectory(
                workspacePath,
                workspaceName,
                DirectorySourceKind.LocalPath,
                accessMode,
                AgentPath: PathHelper.ToWorkspaceAgentPath(workspaceName)));
        }

        string? dataPath = userDirectoryService.GetAgentPrivateDirectoryPath(agentDefinition.Id);
        if (!string.IsNullOrWhiteSpace(dataPath))
        {
            directories.Add(new AllowedDirectory(
                dataPath,
                AgentPrivateLabel,
                DirectorySourceKind.LocalPath,
                WorkspaceAccessMode.ReadWrite,
                AgentPath: PathHelper.SelfAgentPath));
        }

        // The shared folder needs the agent's opt-in and the owner's permission. Grants resolve synchronously,
        // and the permission service never resumes on a captured context.
        if (agentDefinition.IsSharedFolderEnabled &&
            permissions.GetGrantedAsync(userId).GetAwaiter().GetResult().Contains(UserPermissionKeys.SharedFolder))
        {
            directories.Add(new AllowedDirectory(
                userDirectoryService.GetSharedDirectoryPath(),
                SharedFolderLabel,
                DirectorySourceKind.LocalPath,
                WorkspaceAccessMode.ReadWrite,
                AgentPath: PathHelper.SharedAgentPath));
        }

        IEnumerable<AllowedDirectory> runDirectories = standardRunDirectories
            ?? runContextAccessor.Current?.StandardRunDirectories
            ?? [];
        foreach (var directory in runDirectories)
        {
            var grant = directory;
            if (PathHelper.TryGetContainingDirectory(grant.Value, directories, out var configured) &&
                configured?.AccessMode == WorkspaceAccessMode.ReadOnly)
            {
                if (grant.IsWorkingDirectory)
                {
                    throw new UnauthorizedAccessException("The selected project is read-only for this agent.");
                }

                grant = grant with { AccessMode = WorkspaceAccessMode.ReadOnly };
            }

            var duplicate = directories.FindIndex(existing => PathHelper.ArePathsEqual(existing.Value, grant.Value) &&
                existing.AgentPath != PathHelper.SelfAgentPath);
            if (duplicate >= 0)
            {
                var existing = directories[duplicate];
                directories[duplicate] = existing with
                {
                    AccessMode = existing.AccessMode == WorkspaceAccessMode.ReadOnly ? existing.AccessMode : grant.AccessMode,
                    IsWorkingDirectory = existing.IsWorkingDirectory || grant.IsWorkingDirectory
                };
                continue;
            }

            var label = grant.Label;
            var alias = grant.AgentPath ?? PathHelper.ToExtraMountAgentPath(label);
            for (var suffix = 2; directories.Any(existing => existing.AgentPath == alias); suffix++)
            {
                label = $"{grant.Label}-{suffix}";
                alias = PathHelper.ToExtraMountAgentPath(label);
            }

            directories.Add(grant with { Label = label, AgentPath = alias });
        }

        ValidateAgentPathCollisions(directories);
        if (directories.Any(directory => directory.IsWorkingDirectory))
        {
            _ = PathHelper.ResolveWorkingDirectoryHostPath(directories);
        }

        return directories;
    }

    /// <summary>Combines a child's configured access with its parent's shared roots, excluding private data.</summary>
    public IReadOnlyList<AllowedDirectory> ResolveDelegatedDirectories(
        string userId, AgentDefinition child, IReadOnlyList<AllowedDirectory> parentDirectories) =>
        ResolveAllowedDirectories(userId, child, parentDirectories.Where(directory => directory.AgentPath != PathHelper.SelfAgentPath).ToArray());

    /// <summary>Rejects aliases that duplicate another alias or conflict with a native grant.</summary>
    private static void ValidateAgentPathCollisions(IReadOnlyList<AllowedDirectory> directories)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory.AgentPath))
            {
                continue;
            }

            string alias = PathHelper.ToSlashSeparatedRelativePath(directory.AgentPath).TrimEnd('/');
            if (!seen.Add(alias))
            {
                throw new InvalidOperationException($"More than one folder resolves to agent path '{alias}'. Use unique labels.");
            }

            if (!Path.IsPathFullyQualified(alias))
            {
                continue;
            }

            string aliasRoot = Path.GetFullPath(alias);
            foreach (AllowedDirectory candidate in directories.Where(static item => item.Kind == DirectorySourceKind.LocalPath))
            {
                string candidateRoot = Path.GetFullPath(candidate.Value);
                string? overlapRoot = PathHelper.IsPathWithinDirectory(candidateRoot, aliasRoot)
                    ? candidateRoot
                    : PathHelper.IsPathWithinDirectory(aliasRoot, candidateRoot)
                        ? aliasRoot
                        : null;
                if (overlapRoot is null)
                {
                    continue;
                }

                string mapped = Path.GetFullPath(Path.Combine(
                    directory.Value,
                    Path.GetRelativePath(aliasRoot, overlapRoot)));
                if (!PathHelper.ArePathsEqual(mapped, overlapRoot))
                {
                    throw new InvalidOperationException(
                        $"Folder '{directory.Label}' uses agent path '{alias}', which conflicts with folder '{candidate.Label}' at '{candidate.Value}'. Choose a different physical folder.");
                }
            }
        }
    }

    private static IEnumerable<string> OrderDefaultFirst(IEnumerable<string> workspaceNames)
    {
        return workspaceNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => string.Equals(name, UserFileNames.DefaultWorkspaceDirectory, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static name => name, StringComparer.OrdinalIgnoreCase);
    }
}

#pragma warning restore MAAI001
