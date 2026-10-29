using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Conversations;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Skills.Registry;
using Oscova.Agents.Definitions;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;
using Oscova.Agents.Runtime;
using Oscova.Agents.Search;
using Oscova.Agents.Skills;
using System.ComponentModel;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>
/// Lets the active agent discover registry skills and install them for future runs.
/// </summary>
public sealed class SkillRegistryToolset(
    string userId,
    IGitHubSkillRegistryService registry,
    IGitHubSkillInstallService installer,
    SkillRegistrySettings registrySettings,
    IAgentDefinitionRepository agentDefinitions,
    IAgentCacheInvalidator cacheInvalidator,
    RunContextAccessor runContext,
    ILogger<SkillRegistryToolset> logger) : IAgentToolset
{
    private const int DefaultMaxResults = 5;
    private const int MaxResultsCeiling = 10;
    private static readonly string InstallToolName =
        ToolNaming.BuildToolsetQualifiedName<SkillRegistryToolset>(nameof(InstallSkillAsync));

    [DisplayName("search")]
    [AgentTool]
    [Description("Search the configured skill registries for installable skills by intent. Returns up to 'max' matches as JSON. Use skillregistry__inspect to read a skill's full description.")]
    [ToolNetworkScope(NetworkScope.Egress)]
    public async Task<string> SearchSkillsAsync(
        [Description("What capability you are looking for.")] string query,
        [Description("Maximum results to return, from 1 to 10.")] int max = DefaultMaxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) { throw Failed("A skill registry search query is required."); }

        if (registrySettings.Sources.Count == 0)
        {
            throw new ToolFailureException("No skill registry is configured.", "Ask an administrator to add one.");
        }

        var cappedMax = Math.Clamp(max, 1, MaxResultsCeiling);
        var documents = new List<LexicalSearchDocument>();
        var byId = new Dictionary<string, RemoteSkillCatalogEntry>(StringComparer.Ordinal);
        var successfulSources = 0;
        foreach (var source in registrySettings.Sources)
        {
            try
            {
                var catalog = await registry.EnumerateSkillsAsync(source, cancellationToken).ConfigureAwait(false);
                successfulSources++;
                foreach (var warning in catalog.Errors)
                {
                    logger.LogWarning("Skill catalog warning: {Warning}", warning);
                }

                foreach (var entry in catalog.Entries)
                {
                    var id = $"{entry.Source.Owner}/{entry.Source.Repo}/{entry.Source.Branch}/{entry.SkillPath}";
                    if (byId.TryAdd(id, entry))
                    {
                        documents.Add(new LexicalSearchDocument(id, entry.SkillName, Keywords: [entry.SkillPath]));
                    }
                }
            }
            catch (GitHubRegistryException ex)
            {
                logger.LogWarning(ex, "Failed to enumerate skills from {Source}.", source.DisplayName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure enumerating skills from {Source}.", source.DisplayName);
                throw;
            }
        }

        if (successfulSources == 0)
        {
            logger.LogError("All configured skill registries failed during search.");
            throw new ToolFailureException("The skill registries could not be reached.", "Try again later.");
        }

        var rankedIds = LexicalSearchStrategy.Search(query, documents, cappedMax);
        var results = new List<object>(rankedIds.Count);
        foreach (var id in rankedIds)
        {
            var entry = byId[id];
            results.Add(new
            {
                name = entry.SkillName,
                owner = entry.Source.Owner,
                repo = entry.Source.Repo,
                branch = entry.Source.Branch,
                skillPath = entry.SkillPath
            });
        }

        return JsonSerializer.Serialize(new { query, total = results.Count, results });
    }

    [DisplayName("inspect")]
    [AgentTool]
    [Description("Read one skill's full description from a configured registry so you can judge it before installing.")]
    [ToolNetworkScope(NetworkScope.Egress)]
    public async Task<string> InspectSkillAsync(
        [Description("The skill folder path within the repo, for example 'skills/pdf-tools'.")] string skillPath,
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Branch. Required when multiple configured sources share the same owner/repo.")] string? branch = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(skillPath))
        {
            throw Failed("A skill source and skill path are required.");
        }

        var source = registrySettings.FindSource(owner, repo, branch);
        if (source is null)
        {
            throw new ToolFailureException("That skill source is not configured.", "Ask an administrator to add it.");
        }

        var skillName = skillPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(skillName))
        {
            throw Failed("The skill path must include a skill folder name.");
        }

        var entry = new RemoteSkillCatalogEntry { Source = source, SkillName = skillName, SkillPath = skillPath };
        string? description;
        try
        {
            description = await registry.FetchDescriptionAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubRegistryException ex)
        {
            logger.LogError(ex, "Failed to inspect skill {Source}/{Path}.", source.DisplayName, skillPath);
            throw Failed("The skill registry request failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected failure inspecting skill {Source}/{Path}.", source.DisplayName, skillPath);
            throw;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw Failed("No description was found for that skill.");
        }

        return JsonSerializer.Serialize(new
        {
            found = true,
            name = skillName,
            description,
            owner = source.Owner,
            repo = source.Repo,
            branch = source.Branch,
            skillPath
        });
    }

    /// <summary>Describes a pending skill install from the configured source so the user sees exactly where it comes from.</summary>
    public ToolApprovalReview? DescribeInstall(FunctionCallContent call)
    {
        if (call.Name != InstallToolName ||
            ToolApprovalRequestBuilder.TryGetString(call.Arguments, "skillPath") is not { } skillPath ||
            ToolApprovalRequestBuilder.TryGetString(call.Arguments, "owner") is not { } owner ||
            ToolApprovalRequestBuilder.TryGetString(call.Arguments, "repo") is not { } repo ||
            registrySettings.FindSource(owner, repo, ToolApprovalRequestBuilder.TryGetString(call.Arguments, "branch")) is not { } source)
        {
            return null;
        }

        var path = skillPath.Trim('/');
        return new ToolApprovalReview
        {
            Kind = "registry-install",
            Operation = "skill",
            SkillName = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(),
            Publisher = source.Owner,
            SourceRepository = $"https://github.com/{source.Owner}/{source.Repo}/tree/{source.Branch}/{path}"
        };
    }

    [DisplayName("install")]
    [AgentTool]
    [Description("Install a skill from a configured registry and assign it to you.\nRequires user approval.\nThe skill becomes available on your next run.\nIn chat, wait for the user's next message before loading it.")]
    [ToolNetworkScope(NetworkScope.Egress)]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    public async Task<string> InstallSkillAsync(
        [Description("The skill folder path within the repo, for example 'skills/pdf-tools'.")] string skillPath,
        [Description("Repository owner.")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Branch. Required when multiple configured sources share the same owner/repo.")] string? branch = null,
        CancellationToken cancellationToken = default)
    {
        var context = runContext.Current;
        if (context is null || string.IsNullOrWhiteSpace(context.CurrentAgentId))
        {
            throw Failed("No active agent context; cannot install a skill.");
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(skillPath))
        {
            throw Failed("A skill source and skill path are required.");
        }

        var source = registrySettings.FindSource(owner, repo, branch);
        if (source is null)
        {
            throw new ToolFailureException("That skill source is not configured.", "Ask an administrator to add it.");
        }

        var skillName = skillPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(skillName))
        {
            throw Failed("The skill path must include a skill folder name.");
        }

        var status = "installed";
        try
        {
            await installer.InstallAsync(userId, source, skillName, skillPath, progress: null, cancellationToken).ConfigureAwait(false);
        }
        catch (SkillImportConflictException)
        {
            status = "already_available";
        }
        catch (ArgumentException ex)
        {
            logger.LogError(ex, "Agent skill install input was rejected for {Source}/{Path}.", source.DisplayName, skillPath);
            throw Failed("The skill install request is invalid.");
        }
        catch (GitHubRegistryException ex)
        {
            logger.LogError(ex, "Agent skill install failed for {Source}/{Path}.", source.DisplayName, skillPath);
            throw Failed("The skill could not be installed from the registry.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent skill install failed unexpectedly for {Source}/{Path}.", source.DisplayName, skillPath);
            throw;
        }

        SkillAssignmentResult assignment;
        try
        {
            assignment = await SkillToolsetAssignment.AssignToCurrentAgentAsync(
                userId,
                skillName,
                agentDefinitions,
                cacheInvalidator,
                runContext,
                logger,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Installed skill {SkillName} could not be assigned unexpectedly.", skillName);
            throw;
        }

        if (!assignment.Success)
        {
            throw new ToolFailureException(
                status == "installed"
                    ? "The skill was installed but could not be assigned."
                    : "The skill is available but could not be assigned.",
                "Assign it to the agent in Settings.");
        }

        return JsonSerializer.Serialize(new
        {
            status,
            skillName,
            assignedToAgent = assignment.AgentName,
            available = "next_run",
            message = status == "installed"
                ? "Installed and assigned. The skill is available the next time this agent starts a run."
                : "Already installed and assigned. The skill is available the next time this agent starts a run."
        });
    }

    private static ToolFailureException Failed(string reason) => new(reason);
}
