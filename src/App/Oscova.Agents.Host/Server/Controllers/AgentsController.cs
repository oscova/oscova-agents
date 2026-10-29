using Microsoft.Agents.AI;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Llm;
using Oscova.Agents.Providers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.ApiContracts.Prompts;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Definitions;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Infrastructure.Agents;
using Oscova.Agents.Infrastructure.Auth;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Policies;
using Oscova.Agents.Skills;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/agents")]
[IgnoreAntiforgeryToken]
public sealed class AgentsController(
    UserServiceManager userServiceManager,
    IUserDirectoryService userDirectoryService,
    ISharedAgentPersonalContextRepository sharedAgentContextRepository,
    UserManager<ApplicationUser> userManager,
    RuntimeVersionService runtimeVersionService,
    AgentPackageService agentPackages,
    PrebuiltAgentCatalog prebuiltAgents,
    IProfilePictureService profilePictures,
    ThreadRunGuard threadRunGuard,
    IUserPermissionService permissions,
    ILogger<AgentsController> logger)
    : UserScopeControllerBase(userServiceManager)
{
    private const long MaxImportRequestSizeBytes = AgentPackageService.MaxUploadSizeBytes + (8 * 1024L);

    [HttpGet]
    public async Task<ActionResult<AgentConfiguration>> Get(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var appService = userScope.AppService;
        await appService.ReloadAgentDefinitionsAsync();
        await userScope.AgentGoals.LoadAsync();

        return Ok(new AgentConfiguration
        {
            MainAgent = appService.MainAgentDefinition,
            GoalIterationLimits = userScope.AgentGoals.Limits,
            CustomAgents = [.. appService.CustomAgentDefinitions.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)],
            ExposableAgents = [.. appService.CustomAgentDefinitions
                .Where(agent => agent.IsExposedAsTool)
                .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
                .Select(agent => new AgentReference
                {
                    Id = agent.Id,
                    Name = agent.Name,
                    Description = agent.Description
                })],
            ToolGroups = GetToolGroups(appService),
            AvailableSkills = GetAvailableSkills(userScope),
            AvailableModelProviders = ChatModelProviderSelection.GetSelectableProviders(userScope),
            AvailableWorkspaces = userDirectoryService.ListWorkspaces(userScope.UserId),
            AvailableUsers = await GetAvailableUsersAsync(userScope, ct),
            CanManageSharedAgents = userScope.IsInRole("Admin") || userScope.IsInRole("Moderator"),
            CanUseSharedFolder = (await permissions.GetGrantedAsync(userScope.UserId, ct)).Contains(UserPermissionKeys.SharedFolder)
        });
    }

    [HttpPost("custom")]
    public async Task<ActionResult<AgentDefinition>> CreateCustomAgent(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        try
        {
            var agent = await userScope.AppService.CreateCustomAgentAsync();
            return Ok(agent);
        }
        catch (AgentCallableNameCollisionException exception)
        {
            return Conflict(new AgentCallableNameCollisionResult
            {
                AgentName = exception.AgentName,
                CallableName = exception.CallableName
            });
        }
    }

    /// <summary>Lists bundled starters without creating user agents.</summary>
    [HttpGet("prebuilt")]
    public ActionResult<IReadOnlyList<PrebuiltAgentTemplate>> GetPrebuilt() => Ok(prebuiltAgents.GetAll());

    /// <summary>Installs a fresh editable copy of a bundled starter for the current user.</summary>
    [HttpPost("prebuilt/{templateId}/install")]
    public async Task<ActionResult<AgentDefinition>> InstallPrebuilt(string templateId, CancellationToken ct)
    {
        try
        {
            var template = prebuiltAgents.GetAll().FirstOrDefault(item => item.Id == templateId);
            if (template is null) { return NotFound(); }

            var userScope = await GetUserScopeAsync(ct);
            var candidate = new AgentDefinition
            {
                Name = template.Name,
                Description = template.Description,
                SystemInstruction = string.Empty,
                InstructionSource = template.Id,
                InstructionMode = InstructionSelection.Default,
                DefaultInstruction = template.SystemInstruction,
                AllowedToolNames = [.. template.RequestedToolNames],
                IsMemoryEnabled = false
            };
            SanitizeAgentToolReferences(candidate, userScope);
            var created = await userScope.AppService.CreateCustomAgentAsync(candidate, initializeInstructionSource: true);
            // Resolve an opaque picture key only through the existing seed catalog.
            if (template.ProfilePictureKey is { } pictureKey &&
                profilePictures.GetAvailableSeedNames().Contains(pictureKey, StringComparer.Ordinal))
            {
                await profilePictures.ApplyAgentSeedAsync(created.Id, pictureKey, ct);
            }

            return Ok(created);
        }
        catch (AgentCallableNameCollisionException exception)
        {
            return Conflict(new AgentCallableNameCollisionResult
            {
                AgentName = exception.AgentName,
                CallableName = exception.CallableName
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to install prebuilt agent {TemplateId}.", templateId);
            throw;
        }
    }

    [HttpPut("main")]
    public async Task<IActionResult> SaveMain([FromBody] AgentDefinition request, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var mainAgent = userScope.AppService.MainAgentDefinition;
        var validation = ValidateEditableAgent(request, userScope, mainAgent);
        if (validation is not null)
        {
            return validation;
        }

        ApplyAgent(mainAgent, request, allowSharing: false);
        SanitizeAgentToolReferences(mainAgent, userScope);
        // Defensive: IsMain/IsShared are server-controlled; never trust the body to set them.
        mainAgent.IsMain = true;
        mainAgent.IsShared = false;
        await userScope.AppService.SaveMainAgentAsync();

        return NoContent();
    }

    /// <summary>Saves this account's cycle limit under the existing editor authorization.</summary>
    [HttpPut("{agentId}/goal-limit")]
    public async Task<IActionResult> SaveGoalLimit(string agentId, SaveAgentGoalLimitRequest request, CancellationToken ct)
    {
        if (request.MaxIterations is < 1) { return BadRequest("MaxIterations must be 1 to 2147483647, or null for Default."); }

        var scope = await GetUserScopeAsync(ct);
        await scope.AppService.ReloadAgentDefinitionsAsync();
        var agent = scope.AppService.ResolveAgentDefinition(agentId);
        if (agent.Id != agentId) { return NotFound(); }

        var owns = agent.IsMain || agent.OwnerUserId == scope.UserId;
        if (!owns && (!agent.IsShared || !(scope.IsInRole("Admin") || scope.IsInRole("Moderator")))) { return Forbid(); }

        await scope.AgentGoals.SetLimitAsync(agentId, request.MaxIterations);
        return NoContent();
    }

    [HttpPut("custom/{agentId}")]
    public async Task<IActionResult> SaveCustom(string agentId, [FromBody] AgentDefinition request, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var agent = userScope.AppService.CustomAgentDefinitions.FirstOrDefault(existing =>
            string.Equals(existing.Id, agentId, StringComparison.Ordinal));

        if (agent is null)
        {
            return NotFound();
        }

        var canManageSharedAgents = userScope.IsInRole("Admin") || userScope.IsInRole("Moderator");
        var validation = ValidateEditableAgent(request, userScope, agent);
        if (validation is not null)
        {
            return validation;
        }

        var candidate = new AgentDefinition
        {
            Id = agent.Id,
            IsMain = agent.IsMain,
            OwnerUserId = agent.OwnerUserId,
            InstructionSource = agent.InstructionSource,
            DefaultInstruction = agent.DefaultInstruction
        };
        ApplyAgent(candidate, request, canManageSharedAgents);
        SanitizeAgentToolReferences(candidate, userScope);

        try
        {
            if (!await userScope.AppService.SaveCustomAgentAsync(candidate))
            {
                return NotFound();
            }

            await UserServices.RefreshAgentConsumersAsync(userScope.UserId, candidate.Id);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "Could not save custom agent {AgentId} for user {UserId}.", agentId, userScope.UserId);
            return Problem("The agent could not be saved because its stored data could not be updated. Check the logs for details.");
        }
        catch (AgentCallableNameCollisionException exception)
        {
            return Conflict(new AgentCallableNameCollisionResult
            {
                AgentName = exception.AgentName,
                CallableName = exception.CallableName
            });
        }
    }

    [HttpDelete("custom/{agentId}")]
    public async Task<IActionResult> DeleteCustom(string agentId, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        await userScope.AppService.ReloadAgentDefinitionsAsync();
        var agent = userScope.AppService.CustomAgentDefinitions.FirstOrDefault(existing =>
            string.Equals(existing.Id, agentId, StringComparison.Ordinal));

        if (agent is null)
        {
            return NoContent();
        }

        var ownsAgent = string.Equals(agent.OwnerUserId, userScope.UserId, StringComparison.OrdinalIgnoreCase);
        var canManageSharedAgents = userScope.IsInRole("Admin") || userScope.IsInRole("Moderator");
        if (!ownsAgent && (!agent.IsShared || !canManageSharedAgents))
        {
            return Forbid();
        }

        var deletionLeases = new List<IDisposable>();
        var otherScopes = new List<UserServiceScope>();
        try
        {
            var otherUserIds = await userManager.Users
                .Where(user => user.Id != userScope.UserId && user.Id != WellKnownUsers.SystemId)
                .Select(user => user.Id)
                .ToListAsync(ct);

            foreach (var affectedUserId in otherUserIds
                         .Append(userScope.UserId)
                         .OrderBy(static id => id, StringComparer.Ordinal))
            {
                if (!HttpContext.RequestServices.GetRequiredService<Oscova.Agents.Secrets.IContentProtector>().IsAvailable(affectedUserId))
                {
                    continue;
                }

                var affectedScope = string.Equals(
                    affectedUserId,
                    userScope.UserId,
                    StringComparison.Ordinal)
                    ? userScope
                    : await GetUserScopeAsync(affectedUserId, ct);
                var threads = await affectedScope.ChatThreadRepository
                    .GetThreadsAsync(affectedUserId, ct: ct);
                var incognitoThreads = await affectedScope.ChatThreadRepository
                    .GetIncognitoThreadsAsync(affectedUserId, ct);
                var threadIdsToCancel = threads
                    .Concat(incognitoThreads)
                    .Where(thread => string.Equals(thread.AgentId, agent.Id, StringComparison.Ordinal))
                    .Select(thread => thread.Id)
                    .ToArray();

                // Drain every admitted writer while cancelling only runs owned by the deleted agent.
                deletionLeases.Add(await threadRunGuard.CancelAndAcquireUserAsync(
                    affectedUserId,
                    ct,
                    threadIdsToCancel));

                if (affectedScope != userScope)
                {
                    otherScopes.Add(affectedScope);
                }
            }

            // Once cleanup starts, finish the destructive mutation so users are not left partially cleared.
            foreach (var otherScope in otherScopes)
            {
                await otherScope.AppService.ClearAgentReferencesAsync(agent);
            }

            await userScope.AppService.DeleteCustomAgentAsync(agentId);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        finally
        {
            foreach (var deletionLease in deletionLeases)
            {
                deletionLease.Dispose();
            }
        }
    }

    /// <summary>Downloads a portable package for an exportable custom agent.</summary>
    [HttpGet("custom/{agentId}/export")]
    public async Task<IActionResult> ExportCustom(string agentId, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        await userScope.AppService.ReloadAgentDefinitionsAsync();
        var agent = userScope.AppService.CustomAgentDefinitions.FirstOrDefault(existing =>
            string.Equals(existing.Id, agentId, StringComparison.Ordinal));
        if (agent is null)
        {
            return NotFound();
        }

        var ownsAgent = string.Equals(agent.OwnerUserId, userScope.UserId, StringComparison.OrdinalIgnoreCase);
        var canManageSharedAgents = userScope.IsInRole("Admin") || userScope.IsInRole("Moderator");
        if (!ownsAgent && (!agent.IsShared || !canManageSharedAgents))
        {
            return Forbid();
        }

        var package = await agentPackages.ExportAsync(
            agent,
            runtimeVersionService.GetSnapshot().Host.FullVersion,
            userScope.CrewService.GetAgentInstructionDefault(agent),
            ct).ConfigureAwait(false);
        return File(
            package,
            "application/octet-stream",
            AgentPackageService.BuildFileName(agent.Name));
    }

    /// <summary>Imports a portable package as a new custom agent.</summary>
    [HttpPost("custom/import")]
    [RequestSizeLimit(MaxImportRequestSizeBytes)]
    public async Task<ActionResult<ImportedAgentResult>> ImportCustom(
        [FromForm] AgentImportRequest request,
        CancellationToken ct)
    {
        if (request.File is null || request.File.Length == 0)
        {
            return BadRequest(new ErrorResult { Message = "An agent package is required." });
        }

        var userScope = await GetUserScopeAsync(ct);
        var availableToolNames = userScope.CrewService.Tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableWorkspaceNames = userDirectoryService
            .ListWorkspaces(userScope.UserId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var stream = request.File.OpenReadStream();
            var result = await agentPackages.ImportAsync(
                userScope.UserId,
                stream,
                request.Name,
                availableToolNames,
                availableWorkspaceNames,
                providerId => ChatModelProviderSelection.ContainsProvider(userScope, providerId),
                ct).ConfigureAwait(false);

            await userScope.AppService.ReloadAgentDefinitionsAsync();
            return Ok(result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return BadRequest(new ErrorResult { Message = ex.Message });
        }
    }

    /// <summary>Returns the stored selection and resolved default for the existing instruction editor.</summary>
    [HttpGet("{agentId}/instruction")]
    public async Task<ActionResult<PromptDocument>> GetInstruction(string agentId, CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        await scope.AppService.ReloadAgentDefinitionsAsync();
        var agent = scope.AppService.ResolveAgentDefinition(agentId);
        if (agent.Id != agentId) { return NotFound(); }

        if (agent.IsShared && !(scope.IsInRole("Admin") || scope.IsInRole("Moderator"))) { return Forbid(); }

        return Ok(new PromptDocument
        {
            Content = agent.SystemInstruction,
            Mode = agent.InstructionMode,
            DefaultContent = scope.CrewService.GetAgentInstructionDefault(agent),
            FileName = "system-instruction.md"
        });
    }

    [HttpPost("{agentId}/instruction-preview")]
    [Oscova.Agents.Host.Server.Security.RequireUnlockedVault]
    public async Task<ActionResult<AgentInstructionPreview>> PreviewInstruction(
        string agentId, [FromBody] AgentDefinition request, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        userScope.AppService.SyncTools();
        await userScope.AppService.ReloadAgentDefinitionsAsync();
        var saved = userScope.AppService.ResolveAgentDefinition(agentId);
        if (!string.Equals(saved.Id, agentId, StringComparison.Ordinal)) { return NotFound(); }

        var canManageSharedAgents = userScope.IsInRole("Admin") || userScope.IsInRole("Moderator");
        if (saved.IsShared && !canManageSharedAgents) { return Forbid(); }

        var validation = ValidateEditableAgent(request, userScope, saved);
        if (validation is not null) { return (ActionResult)validation; }

        // Apply only editable fields to a detached candidate with server-owned identity.
        var candidate = new AgentDefinition
        {
            Id = saved.Id,
            IsMain = saved.IsMain,
            OwnerUserId = saved.OwnerUserId,
            InstructionSource = saved.InstructionSource,
            DefaultInstruction = saved.DefaultInstruction
        };
        ApplyAgent(candidate, request, allowSharing: canManageSharedAgents && !saved.IsMain);
        SanitizeAgentToolReferences(candidate, userScope);

        try
        {
            ModelProviderConfig? selected = null;
            try
            {
                selected = userScope.AppService.ResolveChatModelProvider(candidate);
            }
            catch (ModelProviderUnavailableException) when (candidate.AgentToolIds.Count == 0)
            {
                // Generic context remains inspectable before a model has been configured.
            }

            var effective = selected;
            IReadOnlyList<AIContextProvider> extraProviders = [];
            ChatClientAgent? previewAgent = null;
            if (selected is not null && (selected.SupportsImages || candidate.AgentToolIds.Count > 0))
            {
                var resolution = await userScope.AppService.GetChatClientResolutionAsync(candidate, selected);
                effective = resolution.EffectiveProvider;
                if (effective.SupportsImages)
                {
                    extraProviders = [new VisionToolProvider(
                        resolution.Client,
                        () => userScope.CrewService.ResolveAllowedDirectories(userScope.UserId, candidate))];
                }

                if (candidate.AgentToolIds.Count > 0)
                {
                    // An explicit provider bypasses the draft parent's cache while building real child declarations.
                    previewAgent = await userScope.AppService.GetOrCreateAgent(
                        userScope.UserId, candidate, selected.Id.ToString(), extraProviders: extraProviders);
                }
            }

            var preview = await userScope.CrewService.PreviewUsageAsync(
                userScope.UserId, candidate, extraProviders, previewAgent, ct);
            preview.ProviderName = effective?.Name ?? string.Empty;
            preview.ModelId = effective?.ModelId ?? string.Empty;
            preview.ContextWindowSize = effective?.ContextWindowSize ?? 0;
            preview.ReservedOutputTokens = preview.ContextWindowSize > 0
                ? ContextWindowBudget.DeriveOutputReserve(preview.ContextWindowSize, effective!.ChatOptions.MaxOutputTokens)
                : 0;
            return Ok(preview);
        }
        catch (ModelProviderUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: exception.Message);
        }
    }

    [HttpGet("shared/{agentId}/personal-context")]
    public async Task<ActionResult<SharedAgentPersonalContext>> GetSharedPersonalContext(string agentId, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var agent = FindSharedAgentForPersonalContext(userScope, agentId);
        if (agent is null)
        {
            return NotFound();
        }

        var content = await sharedAgentContextRepository.GetAsync(userScope.UserId, agent, ct);
        return Ok(new SharedAgentPersonalContext
        {
            AgentId = agentId,
            Content = content
        });
    }

    [HttpPut("shared/{agentId}/personal-context")]
    public async Task<IActionResult> SaveSharedPersonalContext(string agentId, [FromBody] SharedAgentPersonalContextSaveRequest request, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var agent = FindSharedAgentForPersonalContext(userScope, agentId);
        if (agent is null)
        {
            return NotFound();
        }

        await sharedAgentContextRepository.SaveAsync(userScope.UserId, agent, request.Content ?? string.Empty, ct);
        userScope.AppService.CrewService.EvictAgent(agentId);
        return NoContent();
    }

    private async Task<List<SelectableUser>> GetAvailableUsersAsync(UserServiceScope userScope, CancellationToken ct)
    {
        var users = await userManager.Users
            .OrderBy(user => user.UserName)
            .ToListAsync(ct);

        return [.. users
            .Where(user => !string.Equals(user.Id, userScope.UserId, StringComparison.OrdinalIgnoreCase))
            .Select(user => new SelectableUser
            {
                Id = user.Id,
                UserName = user.UserName ?? "User",
                FullName = user.FullName
            })];
    }

    private static List<ToolGroup> GetToolGroups(Core.Conversation.AppService appService)
    {
        return [.. appService.AllTools
            .GroupBy(tool => appService.CrewService.Tools.GetServerName(tool), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ToolGroup
            {
                ServerName = group.Key,
                Tools = [.. group
                    .OrderBy(tool => appService.CrewService.Tools.GetOriginalName(tool), StringComparer.OrdinalIgnoreCase)
                    .Select(tool => new ToolDefinition
                    {
                        QualifiedName = tool.Name,
                        OriginalName = appService.CrewService.Tools.GetOriginalName(tool),
                        Description = tool.Description,
                        DefaultApprovalMode = appService.CrewService.Tools.GetDefaultApprovalMode(tool.Name),
                        NetworkScope = appService.CrewService.Tools.GetNetworkScope(tool.Name)
                    })]
            })];
    }

    private List<SkillDefinition> GetAvailableSkills(UserServiceScope userScope)
    {
        var discoverer = new SkillDiscoverer();
        var skills = discoverer.Discover(userDirectoryService.GetSkillsDirectoryPath(userScope.UserId));

        // Settings only needs assignable skill metadata. Runtime loading happens later in AgentCompositionService via AgentSkillsProvider.
        return [.. skills
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(skill => new SkillDefinition
            {
                Name = skill.Name,
                Description = skill.Description
            })];
    }

    private static void ApplyAgent(AgentDefinition target, AgentDefinition source, bool allowSharing)
    {
        target.Name = source.Name;
        target.Description = source.Description;
        target.SystemInstruction = source.SystemInstruction;
        target.InstructionMode = target.InstructionSource is null ? null : source.InstructionMode;
        target.ModelOverrideId = NormalizeModelOverrideId(source.ModelOverrideId);
        target.UseCallerModelForConsultations = source.UseCallerModelForConsultations;
        target.AllowedToolNames = [.. source.AllowedToolNames];
        target.ToolPolicies = [.. source.ToolPolicies];
        target.IsToolSearchEnabled = source.IsToolSearchEnabled;
        target.PinnedToolNames = [.. source.PinnedToolNames.Where(name => source.AllowedToolNames.Contains(name, StringComparer.OrdinalIgnoreCase))];
        target.IsCompactionEnabled = source.IsCompactionEnabled;
        target.IsMemoryEnabled = source.IsMemoryEnabled;
        target.IsTodoEnabled = source.IsTodoEnabled;
        target.IsSubagentDelegationEnabled = source.IsSubagentDelegationEnabled;
        target.IsLocalOnly = source.IsLocalOnly;
        target.IsExposedAsTool = source.IsExposedAsTool;
        target.AgentToolIds = [.. source.AgentToolIds];
        target.AllowedSkillNames = [.. source.AllowedSkillNames];
        target.AllowedWorkspaces = [.. source.AllowedWorkspaces];
        target.AllowedDirectories = [.. source.AllowedDirectories];
        target.IsSharedFolderEnabled = source.IsSharedFolderEnabled;
        target.IsShared = allowSharing && source.IsShared;
        target.AllowedUserIds = target.IsShared ? [.. source.AllowedUserIds] : [];
    }

    private void SanitizeAgentToolReferences(AgentDefinition agent, UserServiceScope userScope)
    {
        // Normal agent saves use foreground availability; tasks apply their stricter catalogue separately.
        var availableToolNames = userScope.CrewService.Tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = ToolReferenceSanitizer.RemoveUnavailableTools(
            agent.AllowedToolNames,
            agent.PinnedToolNames,
            agent.ToolPolicies,
            availableToolNames);

        LogToolReferenceSanitization("agent", agent.Id, result);
    }

    private void LogToolReferenceSanitization(
        string ownerKind,
        string ownerId,
        ToolReferenceSanitizationResult result)
    {
        if (!result.HasChanges)
        {
            return;
        }

        logger.LogWarning(
            "Removed stale tool references from {OwnerKind} {OwnerId}. Removed allowed tools: {RemovedAllowedToolCount}; removed pinned tools: {RemovedPinnedToolCount}; removed tool policies: {RemovedPolicyToolCount}.",
            ownerKind,
            ownerId,
            result.RemovedAllowedToolNames.Count,
            result.RemovedPinnedToolNames.Count,
            result.RemovedPolicyToolNames.Count);
    }

    private static IActionResult? ValidateEditableAgent(AgentDefinition source, UserServiceScope userScope, AgentDefinition saved)
    {
        if (saved.InstructionSource is not null)
        {
            try { InstructionSelection.Validate(source.InstructionMode); }
            catch (InvalidOperationException ex) { return new BadRequestObjectResult(ex.Message); }
        }

        if (string.IsNullOrWhiteSpace(source.Name))
        {
            return new BadRequestObjectResult("Agent name is required.");
        }

        if (source.AllowedDirectories.Count != 0)
        {
            return new BadRequestObjectResult("Agent external folders are unavailable. Register the folder as a Project.");
        }

        source.ModelOverrideId = NormalizeModelOverrideId(source.ModelOverrideId);
        if (source.ModelOverrideId is null) { return null; }

        return ChatModelProviderSelection.ContainsProvider(userScope, source.ModelOverrideId)
            ? null
            : new BadRequestObjectResult("The selected model provider is no longer available for chat.");
    }

    private static string? NormalizeModelOverrideId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // Shared-agent personal context is only valid for agents already visible in the caller's scoped list.
    private static AgentDefinition? FindSharedAgentForPersonalContext(UserServiceScope userScope, string agentId)
    {
        var agent = userScope.AppService.CustomAgentDefinitions.FirstOrDefault(existing =>
            string.Equals(existing.Id, agentId, StringComparison.Ordinal));

        return agent is { IsShared: true } ? agent : null;
    }

    /// <summary>Describes a custom-agent package upload and its optional replacement name.</summary>
    public sealed class AgentImportRequest
    {
        public IFormFile? File { get; set; }
        public string? Name { get; set; }
    }

}
