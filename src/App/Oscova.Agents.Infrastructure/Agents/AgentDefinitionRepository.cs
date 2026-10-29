using Oscova.Agents.Infrastructure.Toolsets;
using Oscova.Agents.Infrastructure.Tools;
using Oscova.Agents.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Utils;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Definitions;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Data;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Infrastructure.Skills;
using Oscova.Agents.Persistence;
using Oscova.Agents.Policies;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;
using Oscova.Agents.Browsing;
using Oscova.Agents.Plugins;

namespace Oscova.Agents.Infrastructure.Agents;

/// <summary>
/// File-backed repository for agent definitions. Each agent lives in one agent.md file.
/// </summary>
public sealed class AgentDefinitionRepository(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IAgentCatalog catalog,
    AgentDefinitionMutationCoordinator mutationCoordinator,
    YamlConverter yamlCodec,
    Oscova.Agents.Secrets.IContentProtector protector,
    IProfilePictureService profilePictures,
    ILogger<AgentDefinitionRepository> logger,
    IAgentPluginCatalog plugins) : IAgentDefinitionRepository
{
    private const string AgentFileName = UserFileNames.AgentFile;
    private const string AgentToolPoliciesFileName = UserFileNames.AgentToolPoliciesFile;
    private const string MainAgentDirectoryName = UserFileNames.MainAgentDirectory;

    public async Task<AgentDefinition?> GetAgentAsync(string userId, string agentId, CancellationToken ct = default)
    {
        var agentDirectory = catalog.TryGetAgentDirectory(agentId);
        if (agentDirectory is null)
        {
            return null;
        }

        var ownerUserId = catalog.TryGetOwnerUserId(agentId);
        if (ownerUserId is null || !protector.IsAvailable(ownerUserId)) { return null; }

        var agent = ReadFromDisk(GetAgentFilePath(agentDirectory), ownerUserId);
        if (IsOwnedBy(agent, userId))
        {
            return agent;
        }

        return await IsSharedAgentVisibleToUserAsync(agent, userId, ct).ConfigureAwait(false) ? agent : null;
    }

    public Task<AgentDefinition?> GetMainAgentAsync(string userId, CancellationToken ct = default)
    {
        var agentFilePath = Path.Combine(GetAgentsRoot(userId), MainAgentDirectoryName, AgentFileName);
        if (!protector.IsAvailable(userId) || !File.Exists(agentFilePath))
        {
            return Task.FromResult<AgentDefinition?>(null);
        }

        return Task.FromResult<AgentDefinition?>(ReadFromDisk(agentFilePath, userId));
    }

    /// <summary>
    /// Ensures the fixed agents/main/agent.md file exists for a user before chat initialization.
    /// </summary>
    public Task EnsureMainAgentAsync(string userId, CancellationToken ct = default)
    {
        return mutationCoordinator.RunAsync(
            mutationCt => EnsureMainAgentCoreAsync(userId, mutationCt),
            ct);
    }

    private async Task EnsureMainAgentCoreAsync(string userId, CancellationToken ct)
    {
        var mainAgentDirectory = Path.Combine(GetAgentsRoot(userId), MainAgentDirectoryName);
        var agentFilePath = Path.Combine(mainAgentDirectory, AgentFileName);
        Directory.CreateDirectory(mainAgentDirectory);
        EnsureDataDirectory(mainAgentDirectory);

        if (File.Exists(agentFilePath))
        {
            var existing = ReadFromDisk(agentFilePath, userId);
            var missing = new[] { ProductGuideSkillSeed.SkillName, ProductGuideSkillSeed.ResearchSkillName }
                .Where(name => !existing.AllowedSkillNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (missing.Count == 0)
            {
                return;
            }

            foreach (var name in missing) { existing.AllowedSkillNames.Add(name); }

            WriteToDisk(mainAgentDirectory, existing, userId);
            await catalog.ReloadAsync(ct).ConfigureAwait(false);
            return;
        }

        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid().ToString(),
            Description = string.Empty,
            SystemInstruction = string.Empty,
            IsMain = true,
            // Fresh agents discover occasional capabilities while common tools remain direct.
            IsToolSearchEnabled = true,
            IsShared = false,
            OwnerUserId = userId,
            AllowedToolNames =
            [
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.ReadFile)),
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.WriteFile)),
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.EditFile)),
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.ApplyPatch)),
                ToolNaming.BuildToolsetQualifiedName<DirectoryToolset>(nameof(DirectoryToolset.ListDirectory)),
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.Grep)),
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.Glob)),
                ToolNaming.BuildToolsetQualifiedName<PdfToolset>(nameof(PdfToolset.ExtractPdfText)),
                ToolNaming.BuildToolsetQualifiedName<PdfToolset>(nameof(PdfToolset.SearchPdfText)),
                ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.ExecCommandAsync)),
                ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.StartCommandAsync)),
                ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.ReadCommandOutputAsync)),
                ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.KillCommandAsync)),
                ToolNaming.BuildToolsetQualifiedName<HostShellToolset>(nameof(HostShellToolset.ExecHostCommandAsync)),
                ToolNaming.BuildToolsetQualifiedName<DateTimeToolset>(nameof(DateTimeToolset.GetCurrentTime)),
                ToolNaming.BuildToolsetQualifiedName<DateTimeToolset>(nameof(DateTimeToolset.ConvertTime)),
                ToolNaming.BuildToolsetQualifiedName<WebToolset>(nameof(WebToolset.WebFetchAsync)),
                ToolNaming.BuildToolsetQualifiedName<WaitToolset>(nameof(WaitToolset.WaitAsync)),
                UserQuestions.QualifiedToolName,
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.ListTasksAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.InspectTaskAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.ReadRunAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.CreateTaskAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.UpdateTaskAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.DeleteTaskAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.RunTaskAsync)),
                ToolNaming.BuildToolsetQualifiedName<SkillAuthoringToolset>(nameof(SkillAuthoringToolset.CreateSkillAsync)),
                ToolNaming.BuildToolsetQualifiedName<SkillAuthoringToolset>(nameof(SkillAuthoringToolset.UpdateSkillAsync)),
                ToolNaming.BuildToolsetQualifiedName<SkillRegistryToolset>(nameof(SkillRegistryToolset.SearchSkillsAsync)),
                ToolNaming.BuildToolsetQualifiedName<SkillRegistryToolset>(nameof(SkillRegistryToolset.InspectSkillAsync)),
                ToolNaming.BuildToolsetQualifiedName<SkillRegistryToolset>(nameof(SkillRegistryToolset.InstallSkillAsync)),
                ToolNaming.BuildToolsetQualifiedName<McpRegistryToolset>(nameof(McpRegistryToolset.SearchMcpServersAsync)),
                ToolNaming.BuildToolsetQualifiedName<McpRegistryToolset>(nameof(McpRegistryToolset.InstallMcpServerAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotificationToolset>(nameof(NotificationToolset.NotifyUserAsync)),
                ToolNaming.BuildToolsetQualifiedName<ReadLogsTool>(nameof(ReadLogsTool.ReadAsync)),
                ToolNaming.BuildToolsetQualifiedName<ChatHistoryToolset>(nameof(ChatHistoryToolset.ListChatsAsync)),
                ToolNaming.BuildToolsetQualifiedName<ChatHistoryToolset>(nameof(ChatHistoryToolset.ReadChatAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.FindNotesAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.ReadNotesAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.SaveNotesAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.ForgetNotesAsync)),
                ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.ListAsync)),
                ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.SaveAsync)),
                ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.CompleteAsync)),
                ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.RemoveAsync))
            ],
            PinnedToolNames =
            [
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.ReadFile)),
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.WriteFile)),
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.EditFile)),
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.ApplyPatch)),
                ToolNaming.BuildToolsetQualifiedName<DirectoryToolset>(nameof(DirectoryToolset.ListDirectory)),
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.Grep)),
                ToolNaming.BuildToolsetQualifiedName<CodingToolset>(nameof(CodingToolset.Glob)),
                ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.ExecCommandAsync)),
                ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.StartCommandAsync)),
                ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.ReadCommandOutputAsync)),
                ToolNaming.BuildToolsetQualifiedName<ShellToolset>(nameof(ShellToolset.KillCommandAsync)),
                ToolNaming.BuildToolsetQualifiedName<HostShellToolset>(nameof(HostShellToolset.ExecHostCommandAsync)),
                ToolNaming.BuildToolsetQualifiedName<DateTimeToolset>(nameof(DateTimeToolset.GetCurrentTime)),
                ToolNaming.BuildToolsetQualifiedName<WebToolset>(nameof(WebToolset.WebFetchAsync)),
                UserQuestions.QualifiedToolName,
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.ListTasksAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.InspectTaskAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.ReadRunAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.CreateTaskAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.UpdateTaskAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.DeleteTaskAsync)),
                ToolNaming.BuildToolsetQualifiedName<AgentTaskToolset>(nameof(AgentTaskToolset.RunTaskAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.FindNotesAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.ReadNotesAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.SaveNotesAsync)),
                ToolNaming.BuildToolsetQualifiedName<NotesToolset>(nameof(NotesToolset.ForgetNotesAsync)),
                ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.ListAsync)),
                ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.SaveAsync)),
                ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.CompleteAsync)),
                ToolNaming.BuildToolsetQualifiedName<BacklogToolset>(nameof(BacklogToolset.RemoveAsync))
            ],
            // Reusable tools are shared within this user's account through a managed workspace.
            AllowedWorkspaces = [UserFileNames.DefaultWorkspaceDirectory, UserFileNames.ToolsWorkspaceDirectory],
            IsSharedFolderEnabled = true,
            AllowedSkillNames = [ProductGuideSkillSeed.SkillName, ProductGuideSkillSeed.VisualizeSkillName, ProductGuideSkillSeed.ResearchSkillName]
        };

        foreach (var name in ToolNaming.GetToolsetQualifiedNames(typeof(EncryptedFileToolset))) { agent.AllowedToolNames.Add(name); }

        foreach (var name in ToolNaming.GetToolsetQualifiedNames(typeof(VisualizationToolset)))
        { agent.AllowedToolNames.Add(name); }

        var browserContributions = plugins.Plugins.SelectMany(plugin => plugin.Toolsets
            .Where(toolset => typeof(IBrowserCapability).IsAssignableFrom(toolset.ToolsetType))
            .Select(toolset => (toolset.ToolsetType, plugin.Manifest.Id))).ToArray();
        if (browserContributions.Length == 1)
        {
            var (type, origin) = browserContributions[0];
            foreach (var name in ToolNaming.GetToolsetQualifiedNames(type, origin))
            { agent.AllowedToolNames.Add(name); }
        }

        WriteToDisk(mainAgentDirectory, agent, userId);
        await catalog.ReloadAsync(ct).ConfigureAwait(false);

        // Profile pictures resolve agentId through the catalog, so reload must happen first.
        profilePictures.EnsureAgentPicture(agent.Id);
    }

    public async Task<IReadOnlyList<AgentDefinition>> GetAllAgentsAsync(string userId, CancellationToken ct = default)
    {
        var visibleAgents = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in catalog.GetPersonalEntriesForUser(userId))
        {
            var agent = TryReadCatalogEntry(entry);
            if (agent is null)
            {
                continue;
            }

            visibleAgents[agent.Id] = agent;
        }

        foreach (var entry in catalog.GetSharedEntries())
        {
            if (visibleAgents.ContainsKey(entry.AgentId))
            {
                continue;
            }

            var agent = TryReadCatalogEntry(entry);
            if (agent is null)
            {
                continue;
            }

            if (await IsSharedAgentVisibleToUserAsync(agent, userId, ct).ConfigureAwait(false))
            {
                visibleAgents[agent.Id] = agent;
            }
        }

        return [.. visibleAgents.Values.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private AgentDefinition? TryReadCatalogEntry(AgentCatalogEntry entry)
    {
        if (!protector.IsAvailable(entry.OwnerUserId)) { return null; }

        var agentFilePath = GetAgentFilePath(entry.DirectoryPath);
        try
        {
            return ReadFromDisk(agentFilePath, entry.OwnerUserId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Skipping agent file '{AgentFilePath}' because it could not be loaded.",
                agentFilePath);
            return null;
        }
    }

    public Task DeleteAgentAsync(string userId, string agentId, CancellationToken ct = default)
    {
        return mutationCoordinator.RunAsync(
            mutationCt => DeleteAgentCoreAsync(userId, agentId, mutationCt),
            ct);
    }

    private async Task DeleteAgentCoreAsync(string userId, string agentId, CancellationToken ct)
    {
        var agentDirectory = catalog.TryGetAgentDirectory(agentId);
        if (agentDirectory is null)
        {
            return;
        }

        var ownerUserId = catalog.TryGetOwnerUserId(agentId);
        var agent = ReadFromDisk(GetAgentFilePath(agentDirectory), ownerUserId);
        if (!IsOwnedBy(agent, userId))
        {
            if (!agent.IsShared)
            {
                return;
            }

            var isPrivileged = await IsPrivilegedUserAsync(userId, ct).ConfigureAwait(false);
            if (!isPrivileged)
            {
                throw new UnauthorizedAccessException(
                    $"User '{userId}' is not authorized to delete shared agent '{agentId}'.");
            }
        }

        Directory.Delete(agentDirectory, recursive: true);
        await catalog.ReloadAsync(ct).ConfigureAwait(false);
    }

    public Task SaveAgentAsync(string userId, AgentDefinition agent, CancellationToken ct = default, string? creationDirectoryName = null)
    {
        return mutationCoordinator.RunAsync(
            mutationCt => SaveAgentCoreAsync(userId, agent, mutationCt, creationDirectoryName, updateInstructionSource: false),
            ct);
    }

    /// <summary>Updates source data through the same owner authorization and atomic mutation boundary.</summary>
    public Task SaveInstructionSourceAsync(string userId, AgentDefinition agent, CancellationToken ct = default)
        => mutationCoordinator.RunAsync(
            mutationCt => SaveAgentCoreAsync(userId, agent, mutationCt, null, updateInstructionSource: true), ct);

    private async Task SaveAgentCoreAsync(string userId, AgentDefinition agent, CancellationToken ct,
        string? creationDirectoryName, bool updateInstructionSource)
    {
        if (agent.IsMain && agent.IsShared)
        {
            throw new InvalidOperationException("The main agent cannot be shared.");
        }

        var currentDirectory = catalog.TryGetAgentDirectory(agent.Id);
        var ownerUserId = currentDirectory is null
            ? userId
            : catalog.TryGetOwnerUserId(agent.Id) ?? userId;

        AgentDefinition? existingAgent = null;
        if (currentDirectory is not null)
        {
            existingAgent = ReadFromDisk(GetAgentFilePath(currentDirectory), ownerUserId);
            await ValidateExistingAgentWriteAsync(existingAgent, userId, ct).ConfigureAwait(false);

            // Preserve the role of the fixed main slot even if a caller posts stale form state.
            if (existingAgent.IsMain)
            {
                agent.IsMain = true;
                agent.IsShared = false;
            }
        }

        // Ordinary editors cannot forge provenance or overwrite a refreshed default.
        if (!updateInstructionSource)
        {
            agent.InstructionSource = existingAgent?.InstructionSource;
            agent.DefaultInstruction = existingAgent?.DefaultInstruction;
        }

        if (agent.InstructionSource is null) { agent.InstructionMode = null; }
        else { InstructionSelection.Validate(agent.InstructionMode); }

        if (existingAgent?.IsShared != true &&
            agent.IsShared &&
            !await IsPrivilegedUserAsync(userId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException(
                $"User '{userId}' is not authorized to make agent '{agent.Id}' shared.");
        }

        await ValidateCallableNameAsync(ownerUserId, agent, ct).ConfigureAwait(false);

        var targetDirectory = ResolveTargetDirectory(ownerUserId, agent, currentDirectory, creationDirectoryName);
        var renamedDirectory = currentDirectory is not null &&
                               !string.Equals(currentDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase);
        var writeDirectory = renamedDirectory ? currentDirectory! : targetDirectory;
        Directory.CreateDirectory(writeDirectory);
        EnsureDataDirectory(writeDirectory);
        var previousAgentContent = renamedDirectory
            ? File.ReadAllText(GetAgentFilePath(writeDirectory))
            : null;
        var toolPoliciesPath = Path.Combine(writeDirectory, AgentToolPoliciesFileName);
        var previousToolPoliciesContent = renamedDirectory && File.Exists(toolPoliciesPath)
            ? File.ReadAllText(toolPoliciesPath)
            : null;

        agent.OwnerUserId = ownerUserId;
        var directoryMoved = false;
        try
        {
            WriteToDisk(writeDirectory, agent, ownerUserId);
            if (renamedDirectory)
            {
                DirectoryUtil.MoveWithRetry(currentDirectory!, targetDirectory);
                directoryMoved = true;
            }

            await catalog.ReloadAsync(ct).ConfigureAwait(false);
        }
        catch when (renamedDirectory)
        {
            if (directoryMoved)
            {
                DirectoryUtil.MoveWithRetry(targetDirectory, currentDirectory!);
            }

            RestoreTextFile(GetAgentFilePath(currentDirectory!), previousAgentContent);
            RestoreTextFile(Path.Combine(currentDirectory!, AgentToolPoliciesFileName), previousToolPoliciesContent);
            throw;
        }

        profilePictures.EnsureAgentPicture(agent.Id);
    }

    private async Task ValidateCallableNameAsync(string ownerUserId, AgentDefinition agent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agent.Name))
        {
            throw new ArgumentException("Agent name is required.", nameof(agent));
        }

        if (agent.IsMain)
        {
            return;
        }

        var callableName = AgentCallableName.Normalize(agent.Name);
        var possibleCollisions = catalog.GetAllEntries()
            .Where(existing =>
                !existing.IsMain &&
                !string.Equals(existing.AgentId, agent.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    AgentCallableName.Normalize(existing.Name),
                    callableName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (possibleCollisions.Count == 0)
        {
            return;
        }

        var requiresPrivilegedAudience =
            HasRestrictedSharedAudience(agent.IsShared, agent.AllowedUserIds) ||
            possibleCollisions.Any(existing =>
                HasRestrictedSharedAudience(existing.IsShared, existing.AllowedUserIds));
        var privilegedUserIds = requiresPrivilegedAudience
            ? await GetPrivilegedUserIdsAsync(ct).ConfigureAwait(false)
            : [];
        var collision = possibleCollisions.FirstOrDefault(existing =>
            CallableNameAudiencesOverlap(
                ownerUserId,
                agent.IsShared,
                agent.AllowedUserIds,
                existing.OwnerUserId,
                existing.IsShared,
                existing.AllowedUserIds,
                privilegedUserIds));

        if (collision is not null)
        {
            throw new AgentCallableNameCollisionException(agent.Name, callableName, collision.Name);
        }
    }

    private static bool HasRestrictedSharedAudience(
        bool isShared,
        IEnumerable<string> allowedUserIds)
    {
        return isShared && allowedUserIds.Any();
    }

    private static bool CallableNameAudiencesOverlap(
        string firstOwnerUserId,
        bool firstIsShared,
        IEnumerable<string> firstAllowedUserIds,
        string secondOwnerUserId,
        bool secondIsShared,
        IEnumerable<string> secondAllowedUserIds,
        IReadOnlyCollection<string> privilegedUserIds)
    {
        if ((firstIsShared && !firstAllowedUserIds.Any()) ||
            (secondIsShared && !secondAllowedUserIds.Any()))
        {
            return true;
        }

        var firstAudience = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            firstOwnerUserId
        };
        if (firstIsShared)
        {
            firstAudience.UnionWith(firstAllowedUserIds);
            firstAudience.UnionWith(privilegedUserIds);
        }

        var secondAudience = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            secondOwnerUserId
        };
        if (secondIsShared)
        {
            secondAudience.UnionWith(secondAllowedUserIds);
            secondAudience.UnionWith(privilegedUserIds);
        }

        return firstAudience.Overlaps(secondAudience);
    }

    private async Task<IReadOnlyCollection<string>> GetPrivilegedUserIdsAsync(CancellationToken ct)
    {
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await (
                from userRole in db.UserRoles.AsNoTracking()
                join role in db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                where role.Name == "Admin" || role.Name == "Moderator"
                select userRole.UserId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
    }

    private async Task ValidateExistingAgentWriteAsync(AgentDefinition existingAgent, string userId, CancellationToken ct)
    {
        if (IsOwnedBy(existingAgent, userId))
        {
            return;
        }

        if (!existingAgent.IsShared || !await IsPrivilegedUserAsync(userId, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException(
                $"User '{userId}' is not authorized to update agent '{existingAgent.Id}'.");
        }
    }

    private static string ResolveTargetDirectory(string ownerUserId, AgentDefinition agent, string? currentDirectory, string? creationDirectoryName)
    {
        var agentsRoot = GetAgentsRoot(ownerUserId);
        if (agent.IsMain)
        {
            return Path.Combine(agentsRoot, MainAgentDirectoryName);
        }

        if (currentDirectory is null && creationDirectoryName is not null)
        {
            if (!PathHelper.IsSafePathSegment(creationDirectoryName) ||
                string.Equals(creationDirectoryName, MainAgentDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(creationDirectoryName, UserFileNames.SharedAgentsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The new agent directory must be a safe custom-agent folder.", nameof(creationDirectoryName));
            }

            if (!PathHelper.TryResolveContainedPath(agentsRoot, creationDirectoryName, out var selectedDirectory))
            {
                throw new UnauthorizedAccessException("The agent directory escapes the user's agent root.");
            }

            return selectedDirectory;
        }

        var currentDirectoryName = currentDirectory is null ? null : Path.GetFileName(currentDirectory);
        var existingSlugs = GetExistingSlugs(agentsRoot, currentDirectoryName);
        var slug = Slugs.MakeUnique(Slugs.ForAgentFolder(agent.Name), existingSlugs);
        return Path.Combine(agentsRoot, slug);
    }

    private static HashSet<string> GetExistingSlugs(string agentsRoot, string? excludedSlug)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MainAgentDirectoryName,
            // Personal contexts must never become part of an ordinary agent's folder.
            UserFileNames.SharedAgentsDirectory
        };

        if (!Directory.Exists(agentsRoot))
        {
            return slugs;
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(agentsRoot))
        {
            var slug = Path.GetFileName(directoryPath);
            if (!string.IsNullOrWhiteSpace(slug) &&
                !string.Equals(slug, excludedSlug, StringComparison.OrdinalIgnoreCase))
            {
                slugs.Add(slug);
            }
        }

        return slugs;
    }

    private static void EnsureDataDirectory(string agentDirectory)
    {
        Directory.CreateDirectory(Path.Combine(agentDirectory, UserFileNames.AgentPrivateDirectory));
    }

    private AgentDefinition ReadFromDisk(string agentFilePath, string? ownerUserIdFromCatalog)
    {
        var ownerUserId = ownerUserIdFromCatalog
            ?? throw new InvalidOperationException("An agent owner is required to open its definition.");
        var text = EncryptedFileContent.ReadAllText(ownerUserId, agentFilePath, protector)
            ?? throw new FileNotFoundException("Agent definition not found.", agentFilePath);
        return ParseDefinition(agentFilePath, ownerUserId, text);
    }
    private AgentDefinition ParseDefinition(string agentFilePath, string ownerUserIdFromCatalog, string fileText)
    {
        var (yaml, body) = MarkdownFrontmatter.Split(fileText);
        yamlCodec.RequireSchemaVersion(yaml, AgentDefinitionFile.CurrentSchemaVersion);
        var file = yamlCodec.Deserialize<AgentDefinitionFile>(yaml);
        var ownerUserId = string.IsNullOrWhiteSpace(file.OwnerUserId)
            ? ownerUserIdFromCatalog
            : file.OwnerUserId;
        var agentDirectory = Path.GetDirectoryName(agentFilePath)
            ?? throw new InvalidOperationException("Agent file path has no directory.");

        return new AgentDefinition
        {
            Id = file.Id,
            Name = file.Name,
            Description = file.Description,
            SystemInstruction = body,
            InstructionSource = file.InstructionSource,
            InstructionMode = file.InstructionMode,
            DefaultInstruction = file.DefaultInstruction,
            ModelOverrideId = NormalizeModelOverrideId(file.ModelOverrideId),
            UseCallerModelForConsultations = file.UseCallerModelForConsultations,
            AllowedToolNames = file.AllowedToolNames,
            IsToolSearchEnabled = file.IsToolSearchEnabled,
            PinnedToolNames = file.PinnedToolNames,
            IsCompactionEnabled = file.IsCompactionEnabled,
            IsMemoryEnabled = file.IsMemoryEnabled,
            IsTodoEnabled = file.IsTodoEnabled,
            IsSubagentDelegationEnabled = file.IsSubagentDelegationEnabled,
            IsLocalOnly = file.IsLocalOnly,
            IsExposedAsTool = file.IsExposedAsTool,
            AgentToolIds = file.AgentToolIds,
            AllowedSkillNames = file.AllowedSkillNames,
            AllowedWorkspaces = file.AllowedWorkspaces,
            AllowedDirectories = [.. file.AllowedDirectories.Select(ToAllowedDirectory)],
            IsSharedFolderEnabled = file.IsSharedFolderEnabled,
            IsShared = file.IsShared,
            IsMain = file.IsMain,
            OwnerUserId = ownerUserId,
            AllowedUserIds = file.AllowedUserIds,
            ToolPolicies = ReadToolPolicies(agentDirectory, ownerUserIdFromCatalog)
        };
    }

    private List<AgentToolPolicy> ReadToolPolicies(string agentDirectory, string ownerUserId)
    {
        var path = Path.Combine(agentDirectory, AgentToolPoliciesFileName);
        var content = EncryptedFileContent.ReadAllText(ownerUserId, path, protector);
        return ParseToolPolicies(content);
    }

    private List<AgentToolPolicy> ParseToolPolicies(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var file = yamlCodec.Deserialize<AgentToolPoliciesFile>(content);
        return [.. file.Tools
                .Where(policy => !string.IsNullOrWhiteSpace(policy.ToolName))
                .Select(policy => new AgentToolPolicy
                {
                    ToolName = policy.ToolName,
                    ApprovalMode = policy.ApprovalMode
                })];
    }

    private void WriteToolPolicies(string targetDirectory, IEnumerable<AgentToolPolicy> policies, string ownerUserId)
    {
        var overrides = policies
            .Where(ToolPolicyResolver.IsPersistedOverride)
            .Select(policy => new AgentToolPolicyFile
            {
                ToolName = policy.ToolName,
                ApprovalMode = policy.ApprovalMode
            })
            .ToList();

        var finalPath = Path.Combine(targetDirectory, AgentToolPoliciesFileName);
        if (overrides.Count == 0)
        {
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            return;
        }

        var yaml = yamlCodec.Serialize(new AgentToolPoliciesFile { Tools = overrides });
        EncryptedFileContent.WriteAllText(ownerUserId, finalPath, yaml, protector);
    }

    private void WriteToDisk(string targetDirectory, AgentDefinition agent, string ownerUserId)
    {
        var file = new AgentDefinitionFile
        {
            Id = agent.Id,
            Name = agent.Name,
            InstructionSource = agent.InstructionSource,
            InstructionMode = agent.InstructionMode,
            DefaultInstruction = agent.DefaultInstruction,
            Description = agent.Description,
            IsMain = agent.IsMain,
            IsShared = agent.IsShared,
            IsCompactionEnabled = agent.IsCompactionEnabled,
            IsMemoryEnabled = agent.IsMemoryEnabled,
            IsTodoEnabled = agent.IsTodoEnabled,
            IsSubagentDelegationEnabled = agent.IsSubagentDelegationEnabled,
            IsLocalOnly = agent.IsLocalOnly,
            IsExposedAsTool = agent.IsExposedAsTool,
            ModelOverrideId = NormalizeModelOverrideId(agent.ModelOverrideId),
            UseCallerModelForConsultations = agent.UseCallerModelForConsultations,
            OwnerUserId = ownerUserId,
            AllowedToolNames = agent.AllowedToolNames,
            IsToolSearchEnabled = agent.IsToolSearchEnabled,
            PinnedToolNames = agent.PinnedToolNames,
            AgentToolIds = agent.AgentToolIds,
            AllowedSkillNames = agent.AllowedSkillNames,
            AllowedWorkspaces = agent.AllowedWorkspaces,
            AllowedUserIds = agent.AllowedUserIds,
            AllowedDirectories = [.. agent.AllowedDirectories.Select(ToAllowedDirectoryFile)],
            IsSharedFolderEnabled = agent.IsSharedFolderEnabled
        };

        var yaml = yamlCodec.Serialize(file);
        var content = MarkdownFrontmatter.Combine(yaml, agent.SystemInstruction);
        var finalPath = GetAgentFilePath(targetDirectory);
        EncryptedFileContent.WriteAllText(ownerUserId, finalPath, content, protector);

        // Tool policies live beside the encrypted definition and are absent when all policies are default.
        WriteToolPolicies(targetDirectory, agent.ToolPolicies, ownerUserId);
    }

    private static AllowedDirectory ToAllowedDirectory(AllowedDirectoryFile file)
    {
        var kind = Enum.TryParse<DirectorySourceKind>(file.Kind, ignoreCase: true, out var parsedKind)
            ? parsedKind
            : DirectorySourceKind.LocalPath;
        var accessMode = Enum.TryParse<WorkspaceAccessMode>(file.AccessMode, ignoreCase: true, out var parsedAccessMode)
            ? parsedAccessMode
            : WorkspaceAccessMode.ReadOnly;

        return new AllowedDirectory(file.Value, file.Label, kind, accessMode);
    }

    private static AllowedDirectoryFile ToAllowedDirectoryFile(AllowedDirectory directory)
    {
        return new AllowedDirectoryFile
        {
            Value = directory.Value,
            Label = directory.Label,
            Kind = directory.Kind.ToString(),
            AccessMode = directory.AccessMode.ToString()
        };
    }

    private static string? NormalizeModelOverrideId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string GetAgentsRoot(string userId)
    {
        return Path.Combine(PathUtil.UsersPath, userId, UserFileNames.AgentsDirectory);
    }

    private static string GetAgentFilePath(string agentDirectory)
    {
        return Path.Combine(agentDirectory, AgentFileName);
    }

    private static void RestoreTextFile(string path, string? content)
    {
        if (content is null)
        {
            File.Delete(path);
            return;
        }

        FileUtil.WriteAllTextAtomic(path, content);
    }

    private static bool IsOwnedBy(AgentDefinition agent, string userId)
    {
        return string.Equals(agent.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsSharedAgentVisibleToUserAsync(AgentDefinition agent, string userId, CancellationToken ct)
    {
        if (!agent.IsShared)
        {
            return false;
        }

        if (IsOwnedBy(agent, userId))
        {
            return true;
        }

        if (await IsPrivilegedUserAsync(userId, ct).ConfigureAwait(false))
        {
            return true;
        }

        return agent.AllowedUserIds.Count == 0 ||
               agent.AllowedUserIds.Contains(userId, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> IsPrivilegedUserAsync(string userId, CancellationToken ct)
    {
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            return await (
            from userRole in db.UserRoles.AsNoTracking()
            join role in db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            where userRole.UserId == userId &&
                  (role.Name == "Admin" || role.Name == "Moderator")
            select role.Id)
            .AnyAsync(ct).ConfigureAwait(false);
        }
    }
    /// <summary>Reads and validates a file-editor definition or policy before its normal agent save.</summary>
    public AgentDefinition ParseFileText(string userId, string relativePath, string content)
    {
        var root = Path.Combine(PathUtil.UsersPath, userId);
        if (!PathHelper.TryResolveContainedPath(root, relativePath, out var path))
        {
            throw new UnauthorizedAccessException("The agent file is outside the user's directory.");
        }

        var entry = catalog.GetPersonalEntriesForUser(userId).SingleOrDefault(candidate =>
            PathHelper.ArePathsEqual(candidate.DirectoryPath, Path.GetDirectoryName(path)!))
            ;
        if (entry is null)
        {
            if (!string.Equals(Path.GetFileName(path), AgentFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The selected agent definition is unavailable.");
            }

            var created = ParseDefinition(path, userId, content);
            if (string.IsNullOrWhiteSpace(created.Id) || catalog.TryGetAgentDirectory(created.Id) is not null)
            {
                throw new InvalidOperationException("A new definition requires a new, non-empty agent ID.");
            }

            created.OwnerUserId = userId;
            created.IsMain = false;
            return created;
        }

        var agent = ReadFromDisk(GetAgentFilePath(entry.DirectoryPath), userId);
        if (string.Equals(Path.GetFileName(path), AgentFileName, StringComparison.OrdinalIgnoreCase))
        {
            var edited = ParseDefinition(path, userId, content);
            if (!string.Equals(edited.Id, entry.AgentId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The agent ID cannot change in the file editor.");
            }

            edited.OwnerUserId = userId;
            edited.IsMain = agent.IsMain;
            agent = edited;
        }
        else if (string.Equals(Path.GetFileName(path), AgentToolPoliciesFileName, StringComparison.OrdinalIgnoreCase))
        {
            agent.ToolPolicies = ParseToolPolicies(content);
        }
        else
        {
            throw new InvalidOperationException("The selected file is not an agent definition or tool policy.");
        }

        return agent;
    }
}
