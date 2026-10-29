using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Core.ApiContracts.Profile;
using Oscova.Agents.Core.ApiContracts.Admin;
using Oscova.Agents.Core.ApiContracts.Channels;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.ApiContracts.Tasks;
using Oscova.Agents.Core.ApiContracts.Prompts;
using Oscova.Agents.Core.ApiContracts.OAuth;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Core.ApiContracts.Hooks;
using Oscova.Agents.Core.ApiContracts.Dashboard;
using Oscova.Agents.Core.ApiContracts.Achievements;
using Oscova.Agents.Core.ApiContracts.Updates;
using Oscova.Agents.Core.ApiContracts.HostControl;
using Oscova.Agents.Core.ApiContracts.Environment;
using Oscova.Agents.Core.ApiContracts.Images;
using Oscova.Agents.Definitions;
using Oscova.Agents.Core.ApiContracts.Common;
using Oscova.Agents.Core.ApiContracts.Memory;
using Oscova.Agents.Core.ApiContracts.Mcp;
using Oscova.Agents.Core.ApiContracts.Mcp.Registry;
using Oscova.Agents.Core.ApiContracts.Tools;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Logging;
using Oscova.Agents.Core.Mcp;
using Oscova.Agents.Llm;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Tasks;

namespace Oscova.Agents.Host.Services.Api;

public sealed class ManagementApiClient(HttpClient httpClient) : ApiClientBase
{
    /// <summary>Reads the resolved default for an existing agent editor.</summary>
    public async Task<PromptDocument> GetAgentInstructionAsync(string agentId, CancellationToken ct = default) =>
        await httpClient.GetFromJsonAsync<PromptDocument>(ApiRoutes.Agents.Instruction(agentId), ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Agent instruction response was empty.");

    /// <summary>Reads the resolved default for an existing task editor.</summary>
    public async Task<PromptDocument> GetTaskInstructionAsync(string taskId, CancellationToken ct = default) =>
        await httpClient.GetFromJsonAsync<PromptDocument>(ApiRoutes.Tasks.Instruction(taskId), ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Task instruction response was empty.");

    /// <summary>Reads the running Host version when release discovery is unavailable.</summary>
    public async Task<HostVersionResponse> GetHostVersionAsync(CancellationToken ct = default) =>
        await httpClient.GetFromJsonAsync<HostVersionResponse>(ApiRoutes.HostControl.Version, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Host version response was empty.");

    /// <summary>Gets the Host-owned app-data path and validates reset eligibility.</summary>
    public async Task<EnvironmentResetPreview> GetEnvironmentResetPreviewAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Environment.Reset, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<EnvironmentResetPreview>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Environment reset preview response was empty.");
    }

    /// <summary>Gets cached or refreshed application update availability.</summary>
    public async Task<ApplicationUpdateStatus> GetApplicationUpdateStatusAsync(bool refresh, CancellationToken ct = default)
    {
        var route = refresh ? $"{ApiRoutes.ApplicationUpdate.Status}?refresh=true" : ApiRoutes.ApplicationUpdate.Status;
        return await httpClient.GetFromJsonAsync<ApplicationUpdateStatus>(route, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Application update status response was empty.");
    }

    /// <summary>Reads current local Avatar eligibility and retained operation progress.</summary>
    public async Task<AvatarUpdateStatus> GetAvatarUpdateStatusAsync(bool refresh, CancellationToken ct = default) =>
        await httpClient.GetFromJsonAsync<AvatarUpdateStatus>(refresh ? ApiRoutes.AvatarUpdate.Status + "?refresh=true" : ApiRoutes.AvatarUpdate.Status, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Avatar update status response was empty.");

    /// <summary>Starts a confirmed installation on the harness computer.</summary>
    public async Task<AvatarUpdateStatus> StartAvatarUpdateAsync(StartAvatarUpdateRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.AvatarUpdate.Start, request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AvatarUpdateStatus>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Avatar installation response was empty.");
    }

    /// <summary>Starts one confirmed application update.</summary>
    public async Task<ApplicationUpdateStatus> StartApplicationUpdateAsync(
        StartApplicationUpdateRequest request,
        CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.ApplicationUpdate.Start, request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ApplicationUpdateStatus>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Application update response was empty.");
    }

    /// <summary>Records that the operator dismissed a committed application update result.</summary>
    public async Task AcknowledgeApplicationUpdateAsync(string operationId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.ApplicationUpdate.Acknowledge(operationId), null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<AgentConfiguration> GetAgentConfigurationAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<AgentConfiguration>(ApiRoutes.Agents.Configuration, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Agent configuration response was empty.");
    }

    public async Task<AgentDefinition> CreateCustomAgentAsync(CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Agents.Custom, content: null, ct).ConfigureAwait(false);
        await ThrowCallableNameCollisionAsync(response, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentDefinition>(cancellationToken: ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Custom agent response was empty.");
    }

    /// <summary>Loads the bundled starters available for installation.</summary>
    public async Task<IReadOnlyList<PrebuiltAgentTemplate>> GetPrebuiltAgentsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<PrebuiltAgentTemplate>>(ApiRoutes.Agents.Prebuilt, ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Prebuilt agent catalog response was empty.");
    }

    /// <summary>Installs an independent custom agent from a server-selected starter.</summary>
    public async Task<AgentDefinition> InstallPrebuiltAgentAsync(string templateId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Agents.InstallPrebuilt(templateId), content: null, ct)
            .ConfigureAwait(false);
        await ThrowCallableNameCollisionAsync(response, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentDefinition>(cancellationToken: ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Installed agent response was empty.");
    }

    public async Task SaveMainAgentAsync(AgentDefinition definition, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Agents.Main, definition, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Saves the personal goal allowance through the agent editor's existing Save action.</summary>
    public async Task SaveAgentGoalLimitAsync(string agentId, int? limit, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Agents.GoalLimit(agentId),
            new SaveAgentGoalLimitRequest(limit), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task SaveCustomAgentAsync(AgentDefinition definition, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Agents.CustomById(definition.Id), definition, ct).ConfigureAwait(false);
        await ThrowCallableNameCollisionAsync(response, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    private static async Task ThrowCallableNameCollisionAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            try
            {
                var collision = await response.Content
                    .ReadFromJsonAsync<AgentCallableNameCollisionResult>(cancellationToken: ct)
                    .ConfigureAwait(false);
                if (collision is not null &&
                    !string.IsNullOrWhiteSpace(collision.AgentName) &&
                    !string.IsNullOrWhiteSpace(collision.CallableName))
                {
                    throw new AgentCallableNameCollisionException(
                        collision.AgentName,
                        collision.CallableName);
                }
            }
            catch (JsonException)
            {
                // Let the shared error reader surface malformed or legacy conflict responses.
            }
        }
    }

    public async Task DeleteCustomAgentAsync(string agentId, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Agents.CustomById(agentId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Downloads a portable package for a custom agent.</summary>
    public Task<Stream> ExportCustomAgentAsync(string agentId, CancellationToken ct = default) =>
        httpClient.GetStreamAsync(ApiRoutes.Agents.CustomExport(agentId), ct);

    /// <summary>Imports a portable package as a new custom agent.</summary>
    public async Task<ImportedAgentResult> ImportCustomAgentAsync(
        string fileName,
        Stream content,
        string? name,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(content);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = fileName,
            FileNameStar = fileName
        };
        form.Add(fileContent, "File", fileName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            form.Add(new StringContent(name), "Name");
        }

        using var response = await httpClient.PostAsync(ApiRoutes.Agents.CustomImport, form, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ImportedAgentResult>(cancellationToken: ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Agent import response was empty.");
    }

    /// <summary>Estimates the current editor draft without saving it.</summary>
    public async Task<AgentInstructionPreview> GetAgentInstructionPreviewAsync(
        string agentId, AgentDefinition draft, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.Agents.InstructionPreview(agentId), draft, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentInstructionPreview>(cancellationToken: ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Agent context preview response was empty.");
    }

    public async Task<SharedAgentPersonalContext> GetSharedAgentPersonalContextAsync(string agentId, CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<SharedAgentPersonalContext>(ApiRoutes.Agents.SharedPersonalContext(agentId), ct)
.ConfigureAwait(false) ?? new SharedAgentPersonalContext { AgentId = agentId };
    }

    public async Task SaveSharedAgentPersonalContextAsync(string agentId, string content, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            ApiRoutes.Agents.SharedPersonalContext(agentId),
            new SharedAgentPersonalContextSaveRequest { Content = content },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<ModelProviderConfiguration> GetModelProviderConfigurationAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<ModelProviderConfiguration>(ApiRoutes.ModelProviders.Configuration, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Model provider configuration response was empty.");
    }

    /// <summary>Reads estimates and limits without exposing other consumers' usage records.</summary>
    public async Task<IReadOnlyList<ModelBudgetStatus>> GetModelBudgetsAsync(CancellationToken ct = default) =>
        await httpClient.GetFromJsonAsync<List<ModelBudgetStatus>>(ApiRoutes.ModelProviders.Budgets, ct).ConfigureAwait(false) ?? [];

    /// <summary>Returns false when an execution-affecting change needs interruption confirmation.</summary>
    public async Task<bool> SaveModelProviderConfigurationAsync(ModelProviderConfiguration dto, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.ModelProviders.Configuration, dto, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) { return false; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return true;
    }

    public async Task<ProviderTestResult> TestModelProviderAsync(ModelProviderConfig config, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.ModelProviders.Test, config, timeout.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ProviderTestResult>(cancellationToken: timeout.Token)
.ConfigureAwait(false) ?? new ProviderTestResult();
    }

    public async Task<AvailableModelsResult> GetAvailableModelsAsync(ModelProviderConfig config, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.ModelProviders.AvailableModels, config, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AvailableModelsResult>(cancellationToken: ct)
.ConfigureAwait(false) ?? new AvailableModelsResult();
    }

    public async Task<IReadOnlyList<string>> GetLocalModelsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<string>>(ApiRoutes.ModelProviders.LocalModels, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<McpConfiguration> GetMcpConfigurationAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<McpConfiguration>(ApiRoutes.Mcp.Configuration, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("MCP configuration response was empty.");
    }

    public async Task<bool> SaveMcpConfigurationAsync(IEnumerable<McpServerConfig> servers, CancellationToken ct = default, bool confirmInterruption = false)
    {
        using var response = await httpClient.PutAsJsonAsync(
            ApiRoutes.Mcp.Configuration,
            new SaveMcpConfigurationRequest { Servers = [.. servers.Select(server => server.Clone())], ConfirmInterruption = confirmInterruption },
            ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) { return false; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return true;
    }

    public async Task<McpServerSaveResult> AddMcpServerAsync(McpServerConfig server, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.Mcp.Servers, server, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<McpServerSaveResult>(cancellationToken: ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("MCP server response was empty.");
    }

    public async Task<bool> DeleteMcpServerAsync(Guid serverId, CancellationToken ct = default, bool confirmInterruption = false)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Mcp.ServerById(serverId) + "?confirmInterruption=" + confirmInterruption, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) { return false; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return true;
    }

    public async Task<McpServerReconnectResult?> RestartMcpServerAsync(Guid serverId, CancellationToken ct = default,
        bool confirmed = false)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Mcp.Restart(serverId) +
            $"?confirmed={confirmed}", content: null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) { return null; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<McpServerReconnectResult>(cancellationToken: ct)
.ConfigureAwait(false) ?? new McpServerReconnectResult();
    }

    public async Task<McpServerReconnectResult?> ReconnectAllMcpServersAsync(CancellationToken ct = default,
        bool confirmed = false)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Mcp.ReconnectAll +
            $"?confirmed={confirmed}", content: null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) { return null; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<McpServerReconnectResult>(cancellationToken: ct)
.ConfigureAwait(false) ?? new McpServerReconnectResult();
    }

    /// <summary>
    /// Searches the official MCP Registry through the host API and returns catalog entries for the server editor.
    /// </summary>
    public async Task<McpRegistrySearchResponse> SearchMcpRegistryAsync(string query, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.McpRegistry.Search(query), ct).ConfigureAwait(false);
        McpRegistrySearchResponse result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<McpRegistrySearchResponse>(cancellationToken: ct)
.ConfigureAwait(false) ?? new McpRegistrySearchResponse();
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            result = new McpRegistrySearchResponse();
        }

        if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(result.Error))
        {
            result.Error = $"MCP registry search failed ({(int)response.StatusCode}).";
        }

        return result;
    }

    public async Task<ToolGovernanceSummary> GetToolGovernanceSummaryAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<ToolGovernanceSummary>(ApiRoutes.ToolGovernance.Summary, ct)
.ConfigureAwait(false) ?? new ToolGovernanceSummary();
    }

    public async Task SaveToolGovernanceAsync(UpdateToolGovernanceRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            ApiRoutes.ToolGovernance.ByTool(request.QualifiedName),
            request,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<AdminSnapshot> GetAdminSnapshotAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<AdminSnapshot>(ApiRoutes.Admin.Snapshot, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Admin snapshot response was empty.");
    }

    public async Task<DashboardSnapshot> GetDashboardSnapshotAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<DashboardSnapshot>(ApiRoutes.Dashboard.Snapshot, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Dashboard snapshot response was empty.");
    }

    /// <summary>Retrieves the signed-in user's derived achievement summary.</summary>
    public async Task<AchievementSummary> GetAchievementSummaryAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<AchievementSummary>(ApiRoutes.Achievements.Summary, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Achievement summary response was empty.");
    }

    public async Task<OAuthProviderAdminConfiguration> GetOAuthProviderConfigurationAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<OAuthProviderAdminConfiguration>(ApiRoutes.OAuth.AdminProviders, ct)
.ConfigureAwait(false) ?? new OAuthProviderAdminConfiguration();
    }

    public async Task SaveOAuthProviderConfigurationAsync(string provider, SaveOAuthProviderRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.OAuth.AdminProvider(provider), request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task SaveAuthSettingsAsync(AuthMode mode, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            ApiRoutes.Admin.AuthSettings,
            new SaveAuthSettingsRequest { Mode = mode },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<SuccessResult> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.Admin.Users, request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the selected account's current execution permission grants.</summary>
    public async Task<IReadOnlyList<string>> GetUserPermissionsAsync(string userId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Admin.UserPermissions(userId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<string[]>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The server returned no permission grants.");
    }

    /// <summary>Saves the operator's execution permission selection for an account.</summary>
    public async Task<SuccessResult> SaveUserPermissionsAsync(
        string userId, IReadOnlyList<string> granted, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Admin.UserPermissions(userId), granted, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<SuccessResult> SaveUserProfileAsync(string userId, SaveUserProfileRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Admin.UserProfile(userId), request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<SuccessResult> SaveUserRolesAsync(string userId, SaveUserRolesRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Admin.UserRoles(userId), request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<SuccessResult> ResetPasswordAsync(string userId, ResetPasswordRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Admin.UserPasswordReset(userId), request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<SuccessResult> SaveUserPinAsync(string userId, SaveUserPinRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Admin.UserPin(userId), request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<SuccessResult> ClearUserPinAsync(string userId, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Admin.UserPin(userId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<SuccessResult> DeleteUserAsync(string userId, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Admin.UserById(userId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<UserProfile> GetProfileAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<UserProfile>(ApiRoutes.Profile.Base, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Profile response was empty.");
    }

    public async Task<SuccessResult> SaveProfileAsync(SaveProfileRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Profile.Base, request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<VaultStatus> GetVaultStatusAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<VaultStatus>(
            ApiRoutes.Profile.Vault,
            ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Vault Password status was empty.");
    }

    public async Task<VaultStatus> SetVaultPasswordAsync(
        SetVaultPasswordRequest request,
        CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Profile.VaultSetup,
            request,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<VaultStatus>(cancellationToken: ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Vault Password status was empty.");
    }

    public async Task<VaultStatus> UnlockVaultAsync(
        UnlockVaultRequest request,
        CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Profile.VaultUnlock,
            request,
            ct).ConfigureAwait(false);
        await EnsureVaultSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<VaultStatus>(cancellationToken: ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Vault Password status was empty.");
    }

    public async Task<VaultStatus> ChangeVaultPasswordAsync(
        ChangeVaultPasswordRequest request,
        CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Profile.VaultChange,
            request,
            ct).ConfigureAwait(false);
        await EnsureVaultSuccessAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<VaultStatus>(cancellationToken: ct)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Vault Password status was empty.");
    }

    /// <summary>Preserves cooldown metadata for the forms instead of flattening it into a message.</summary>
    private static async Task EnsureVaultSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var error = await response.Content.ReadFromJsonAsync<VaultRetryErrorResult>(cancellationToken: ct).ConfigureAwait(false);
            if (error is null || error.RetryUntil == default)
            {
                throw new InvalidOperationException("Vault retry response did not include a valid deadline.");
            }

            throw new VaultRetryException(error.Message, error.RetryUntil);
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Saves both duress credentials and their complete shared policy.</summary>
    public async Task<SuccessResult> SaveDuressCredentialsAsync(
        SaveDuressCredentialsRequest request,
        CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            ApiRoutes.Profile.DuressCredentials,
            request,
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await ReadSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<TaskConfiguration> GetTaskConfigurationAsync(
        bool includeReservedTasks = true,
        CancellationToken ct = default)
    {
        var route = $"{ApiRoutes.Tasks.Base}?includeReservedTasks={includeReservedTasks}";
        return await httpClient.GetFromJsonAsync<TaskConfiguration>(route, ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Task configuration response was empty.");
    }

    /// <summary>Retrieves the current definition, or null when the task has been removed.</summary>
    public async Task<AgentTaskDefinition?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Tasks.ById(taskId), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { return null; }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentTaskDefinition>(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<AgentTaskDefinition> CreateTaskAsync(AgentTaskDefinition definition, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.Tasks.Base, definition, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentTaskDefinition>(cancellationToken: ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Task response was empty.");
    }

    public async Task<AgentTaskDefinition> SaveTaskAsync(AgentTaskDefinition definition, CancellationToken ct = default, bool preserveEnabled = false)
    {
        var route = ApiRoutes.Tasks.ById(definition.Id);
        if (preserveEnabled) { route += "?preserveEnabled=true"; }

        using var response = await httpClient.PutAsJsonAsync(route, definition, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentTaskDefinition>(cancellationToken: ct)
.ConfigureAwait(false) ?? throw new InvalidOperationException("Task response was empty.");
    }

    /// <summary>Updates the enabled switch without submitting a stale task definition.</summary>
    public async Task SetTaskEnabledAsync(string taskId, bool isEnabled, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.Tasks.Enabled(taskId), isEnabled, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Tasks.ById(taskId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AgentTaskRun>> GetTaskRunsAsync(string taskId, int limit = 20, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Tasks.Runs(taskId, limit), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<List<AgentTaskRun>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// Retrieves one task run with rehydrated messages for the session inspector.
    /// </summary>
    public async Task<AgentTaskRun?> GetTaskRunAsync(string taskId, string runId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Tasks.Run(taskId, runId), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentTaskRun>(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task DeleteTaskRunAsync(string taskId, string runId, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Tasks.Run(taskId, runId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EgressAuditEntry>> GetRunEgressAsync(string taskId, string runId, CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<EgressAuditEntry>>(ApiRoutes.Tasks.RunEgress(taskId, runId), ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// Retrieves one filtered page of egress audit rows for the dashboard audit dialog.
    /// </summary>
    public async Task<EgressAuditPage> GetEgressAuditPageAsync(
        string scope,
        EgressAuditQueryRequest request,
        CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<EgressAuditPage>(
            ApiRoutes.EgressAudit.Query(
                scope,
                request.Page,
                request.PageSize,
                request.FromUtc,
                request.ToUtc,
                request.Kind,
                request.Source,
                request.SearchTerm),
            ct).ConfigureAwait(false) ?? new EgressAuditPage
            {
                Entries = [],
                CurrentPage = Math.Max(1, request.Page),
                PageSize = Math.Clamp(request.PageSize, 1, 100),
                TotalCount = 0,
                TotalPages = 1,
                Summary = new EgressAuditSummary(0, 0, 0, 0)
            };
    }

    public async Task<TriggerTaskResult> TriggerTaskAsync(string taskId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Tasks.Trigger(taskId), content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<TriggerTaskResult>(cancellationToken: ct)
.ConfigureAwait(false) ?? new TriggerTaskResult();
    }

    public async Task<HookSettingsSummary> GetHookSettingsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<HookSettingsSummary>(ApiRoutes.AdminHooks.Settings, ct)
.ConfigureAwait(false) ?? new HookSettingsSummary();
    }

    public async Task<HookSettingsSummary> SaveHookSettingsAsync(UpdateHookSettingsRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.AdminHooks.Settings, request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<HookSettingsSummary>(cancellationToken: ct)
.ConfigureAwait(false) ?? new HookSettingsSummary();
    }

    public async Task<CreatedHookApiKey> CreateHookApiKeyAsync(CreateHookApiKeyRequest request, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsJsonAsync(ApiRoutes.AdminHooks.ApiKeys, request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<CreatedHookApiKey>(cancellationToken: ct)
.ConfigureAwait(false) ?? new CreatedHookApiKey();
    }

    public async Task SetHookApiKeyEnabledAsync(string id, bool isEnabled, CancellationToken ct = default)
    {
        using var response = await httpClient.PatchAsJsonAsync(
            ApiRoutes.AdminHooks.ApiKeyById(id),
            new UpdateHookApiKeyRequest { IsEnabled = isEnabled },
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task DeleteHookApiKeyAsync(string id, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.AdminHooks.ApiKeyById(id), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HookSecretCredentialSummary>> GetHookSecretCredentialsAsync(CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<HookSecretCredentialSummary>>(ApiRoutes.AdminHooks.SecretCredentials, ct).ConfigureAwait(false) ?? [];
    }

    public async Task SaveHookSecretCredentialAsync(SaveHookSecretCredentialRequest request, CancellationToken ct = default)
    {
        var key = string.IsNullOrWhiteSpace(request.CredentialKey) ? request.Alias : request.CredentialKey;
        using var response = await httpClient.PutAsJsonAsync(ApiRoutes.AdminHooks.SecretCredentialByKey(key), request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task DeleteHookSecretCredentialAsync(string credentialKey, CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.AdminHooks.SecretCredentialByKey(credentialKey), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Loads saved values and active metadata through the actionable error path.</summary>
    public async Task<ChannelConfiguration> GetChannelConfigurationAsync(string channelId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(ApiRoutes.Channels.ById(channelId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChannelConfiguration>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Channel configuration response was empty.");
    }

    /// <summary>Applies plugin-saved configuration and returns any separate restart failure.</summary>
    public async Task<ChannelSaveResult> ApplyChannelConfigurationAsync(string channelId, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Channels.Apply(channelId), content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChannelSaveResult>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Channel save response was empty.");
    }

    /// <summary>Starts the saved channel connection.</summary>
    public Task<ChannelStatus> StartChannelAsync(string channelId, CancellationToken ct = default) =>
        SendChannelCommandAsync(ApiRoutes.Channels.Start(channelId), ct);

    /// <summary>Stops the channel connection and its admitted turns.</summary>
    public Task<ChannelStatus> StopChannelAsync(string channelId, CancellationToken ct = default) =>
        SendChannelCommandAsync(ApiRoutes.Channels.Stop(channelId), ct);

    private async Task<ChannelStatus> SendChannelCommandAsync(string route, CancellationToken ct)
    {
        using var response = await httpClient.PostAsync(route, content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ChannelStatus>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Channel status response was empty.");
    }
    public async Task<IReadOnlyList<string>> GetLogFilesAsync(
        string scope = "self",
        string? targetUserId = null,
        CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<List<string>>(ApiRoutes.Logs.Files(scope, targetUserId), ct).ConfigureAwait(false) ?? [];
    }

    public async Task<LogPage> GetLogPageAsync(
        string? fileName,
        string? level,
        string? searchTerm,
        int page,
        int pageSize,
        string scope = "self",
        string? targetUserId = null,
        CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<LogPage>(
                   ApiRoutes.Logs.Page(fileName, level, searchTerm, page, pageSize, scope, targetUserId),
                   ct)
.ConfigureAwait(false) ?? new LogPage
               {
                   Entries = [],
                   CurrentPage = 1,
                   TotalPages = 1,
                   TotalCount = 0,
                   FileName = fileName ?? string.Empty
               };
    }

    public async Task ClearLogFileAsync(
        string fileName,
        string scope = "self",
        string? targetUserId = null,
        CancellationToken ct = default)
    {
        using var response = await httpClient.DeleteAsync(ApiRoutes.Logs.Clear(fileName, scope, targetUserId), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves one page of stored chat-memory chunks for the shell inspector dialog.
    /// </summary>
    public async Task<ChatMemoryPage> GetChatMemoryPageAsync(
        string? agentId,
        string? threadId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        return await httpClient.GetFromJsonAsync<ChatMemoryPage>(
                   ApiRoutes.ChatMemory.Page(agentId, threadId, page, pageSize),
                   ct)
.ConfigureAwait(false) ?? new ChatMemoryPage
               {
                   Entries = [],
                   CurrentPage = Math.Max(1, page),
                   HasNextPage = false
               };
    }

    /// <summary>
    /// Uploads a replacement profile picture for the signed-in user. Overwrites any existing picture.
    /// </summary>
    public async Task UploadUserProfilePictureAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        using var content = BuildImageContent(stream, fileName);
        using var response = await httpClient.PostAsync(ApiRoutes.ProfilePictures.User, content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieves the seed profile pictures available to the gallery.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetProfilePictureSeedsAsync(CancellationToken ct = default)
    {
        var result = await httpClient.GetFromJsonAsync<ImageSeedList>(ApiRoutes.ProfilePictures.Seeds, ct).ConfigureAwait(false);
        return result?.Seeds ?? [];
    }

    /// <summary>
    /// Applies a packaged seed picture to the signed-in user's profile picture.
    /// </summary>
    public async Task ApplyUserSeedPictureAsync(string seedName, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.ProfilePictures.UserSeed(seedName), content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads a replacement profile picture for an agent. Overwrites any existing picture.
    /// Server-side authorization enforces admin/moderator access for shared agents.
    /// </summary>
    public async Task UploadAgentProfilePictureAsync(string agentId, Stream stream, string fileName, CancellationToken ct = default)
    {
        using var content = BuildImageContent(stream, fileName);
        using var response = await httpClient.PostAsync(ApiRoutes.ProfilePictures.AgentById(agentId), content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a packaged seed picture to an agent's profile picture.
    /// </summary>
    public async Task ApplyAgentSeedPictureAsync(string agentId, string seedName, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.ProfilePictures.AgentSeedById(agentId, seedName), content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Retrieves the packaged wallpapers available to the gallery.</summary>
    public async Task<IReadOnlyList<string>> GetWallpaperSeedsAsync(CancellationToken ct = default)
    {
        var result = await httpClient.GetFromJsonAsync<ImageSeedList>(ApiRoutes.Wallpapers.Seeds, ct).ConfigureAwait(false);
        return result?.Seeds ?? [];
    }

    /// <summary>Uploads a replacement wallpaper for the signed-in user.</summary>
    public async Task UploadUserWallpaperAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        using var content = BuildImageContent(stream, fileName);
        using var response = await httpClient.PostAsync(ApiRoutes.Wallpapers.User, content, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    /// <summary>Applies a packaged wallpaper to the signed-in user.</summary>
    public async Task ApplyUserSeedWallpaperAsync(string seedName, CancellationToken ct = default)
    {
        using var response = await httpClient.PostAsync(ApiRoutes.Wallpapers.UserSeed(seedName), content: null, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    private static MultipartFormDataContent BuildImageContent(Stream stream, string fileName)
    {
        var streamContent = new StreamContent(stream);
        return new MultipartFormDataContent
        {
            { streamContent, "file", string.IsNullOrWhiteSpace(fileName) ? "upload" : fileName }
        };
    }

    private static async Task<SuccessResult> ReadSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        return await response.Content.ReadFromJsonAsync<SuccessResult>(cancellationToken: ct)
.ConfigureAwait(false) ?? new SuccessResult();
    }

}
