using System.Globalization;
using Oscova.Agents.Core.Interfaces;

namespace Oscova.Agents.Host.Constants;

/// <summary>Provides the application's API routes from one compile-time-checked source.</summary>
public static class ApiRoutes
{
    /// <summary>Builds owned project and conversation inspection routes.</summary>
    public static class Nodes
    {
        public const string Root = "api/nodes";
        public static string Node(string nodeId) => $"{Root}/{Uri.EscapeDataString(nodeId)}";
    }

    public static class Projects
    {
        public static string Download(string? projectId, string? threadId, string rootId, string path, string? agentId = null) =>
            $"{Read(projectId, threadId, "download", agentId)}?rootId={Uri.EscapeDataString(rootId)}&path={Uri.EscapeDataString(path)}";
        public const string Catalog = "api/projects";
        public static string Project(string id) => $"{Catalog}/{Uri.EscapeDataString(id)}";
        public static string Read(string? projectId, string? threadId, string action, string? agentId = null)
        {
            var root = projectId is null ? "api/chat" : Project(projectId);
            if (projectId is null && threadId is null && !string.IsNullOrEmpty(agentId))
            { return $"{root}/agents/{Uri.EscapeDataString(agentId)}/{action}"; }

            return threadId is null ? $"{root}/{action}" : $"{root}/threads/{Uri.EscapeDataString(threadId)}/{action}";
        }
    }

    /// <summary>Provides Avatar Bridge routes for runtime, speech, and visual settings.</summary>
    public static class AvatarBridge
    {
        private const string Base = "api/avatarbridge";

        public const string Settings = $"{Base}/settings";
        public const string LocalEngineInstalled = $"{Base}/local-engine/installed";

        public const string Sync = $"{Base}/sync";
        public const string PairingComplete = $"{Base}/pairing/complete";

        /// <summary>Builds the local engine start route. The caller must URI-encode <paramref name="bridgeUrl"/>.</summary>
        public static string StartLocalEngine(string bridgeUrl) => $"{Base}/local-engine/start?bridgeUrl={bridgeUrl}";

        /// <summary>Builds the connection-settings route. The caller must URI-encode both values.</summary>
        public static string ConnectionSettings(string baseUrl, string accessMode) =>
            $"{Base}/settings/connection?baseUrl={baseUrl}&accessMode={accessMode}";

        /// <summary>Builds the pairing-start route. The caller must URI-encode <paramref name="baseUrl"/>.</summary>
        public static string PairingStart(string baseUrl) => $"{Base}/pairing/start?baseUrl={baseUrl}";

        public const string Health = $"{Base}/health";
        public const string Avatars = $"{Base}/avatars";
        public const string AvatarsSelect = $"{Base}/avatars/select";
        public const string Hair = $"{Base}/hair";
        public const string HairSelect = $"{Base}/hair/select";
        public const string Clothing = $"{Base}/clothing";
        public const string ClothingSelect = $"{Base}/clothing/select";
        public const string Emotions = $"{Base}/emotions";
        public const string EmotionsSet = $"{Base}/emotions/set";
        public const string Resolution = $"{Base}/resolution";
        public const string Shutdown = $"{Base}/shutdown";

        /// <summary>Provides avatar speech-control routes.</summary>
        public static class Speech
        {
            private const string Base = $"{AvatarBridge.Base}/speech";

            public const string Speak = $"{Base}/speak";
            public const string Queue = $"{Base}/queue";
            public const string QueueFile = $"{Base}/queue-file";
            public const string Stop = $"{Base}/stop";
            public const string Volume = $"{Base}/volume";
        }

        /// <summary>Provides avatar background routes.</summary>
        public static class Background
        {
            private const string Base = $"{AvatarBridge.Base}/background";

            public const string Color = $"{Base}/color";
        }

        /// <summary>Provides avatar message-rendering routes.</summary>
        public static class Message
        {
            private const string Base = $"{AvatarBridge.Base}/message";

            public const string Text = $"{Base}/text";
            public const string Clear = $"{Base}/clear";
        }

        /// <summary>Provides avatar graphics routes.</summary>
        public static class Graphics
        {
            private const string Base = $"{AvatarBridge.Base}/graphics";

            public const string Preset = $"{Base}/preset";
        }

        public const string PresentationSettings = $"{Base}/presentation/settings";

    }

    /// <summary>Provides host lifecycle routes used by the CLI and the local control channel.</summary>
    public static class HostControl
    {
        private const string Base = "api/host";

        public const string Status = $"{Base}/status";
        public const string Version = $"{Base}/version";
        public const string Restart = $"{Base}/restart";
        public const string Shutdown = $"{Base}/shutdown";
    }

    /// <summary>Provides speech-synthesis and playback routes.</summary>
    public static class SpeechSynthesis
    {
        private const string Base = "api/speechsynthesis";

        public const string Available = $"{Base}/available";
        public const string Voices = $"{Base}/voices";
        public const string VoicesPreview = $"{Base}/voices/preview";
        public static string Start(string sessionId) => $"{Base}/start/{sessionId}";
        public static string Synthesize(string sessionId) => $"{Base}/synthesize/{sessionId}";
        public static string Cache(string sessionId) => $"{Base}/cache/{sessionId}";
        public static string Stop(string sessionId) => $"{Base}/stop/{sessionId}";
        public const string Download = $"{Base}/download";
        public const string Progress = $"{Base}/progress";
        public const string Cancel = $"{Base}/cancel";
    }

    /// <summary>Provides speech-recognition routes.</summary>
    public static class SpeechRecognition
    {
        private const string Base = "api/speechrecognition";

        public const string Available = $"{Base}/available";
        public const string Transcribe = $"{Base}/transcribe";
        public const string Download = $"{Base}/download";
        public const string Progress = $"{Base}/progress";
        public const string Cancel = $"{Base}/cancel";
    }

    /// <summary>Provides routes for installed and packaged skills.</summary>
    public static class Skills
    {
        public const string Base = "api/skills";
        public const string Import = $"{Base}/import";

        public static string Delete(string skillName) => $"{Base}/{Uri.EscapeDataString(skillName)}";
    }

    public static class SkillRegistry
    {
        private const string Base = "api/skill-registry";

        public const string Catalog = $"{Base}/catalog";
        public const string Install = $"{Base}/install";

        public static string Description(string owner, string repo, string branch, string skillPath) =>
            $"{Base}/description?owner={Uri.EscapeDataString(owner)}&repo={Uri.EscapeDataString(repo)}&branch={Uri.EscapeDataString(branch)}&skillPath={Uri.EscapeDataString(skillPath)}";

        public const string Settings = "api/skill-registry-settings";
        public const string AdminCredential = "api/admin/github-skills-credential";
    }

    public static class PluginRegistry
    {
        private const string Base = "api/plugin-registry";

        public const string Install = $"{Base}/install";

        public static string Search(string query) =>
            $"{Base}/search?q={Uri.EscapeDataString(query)}";

        public static string Metadata(string packageId) =>
            $"{Base}/metadata/{Uri.EscapeDataString(packageId)}";
    }

    public static class McpRegistry
    {
        private const string Base = "api/mcp-registry";

        public static string Search(string query) =>
            $"{Base}/search?q={Uri.EscapeDataString(query)}";
    }

    /// <summary>Provides authentication routes.</summary>
    public static class Auth
    {
        private const string Base = "auth";

        public const string Login = $"{Base}/login";

        /// <summary>Builds a login-ticket route with an optional return URL.</summary>
        public static string LoginTicket(string token, string? returnUrl = null)
        {
            var url = $"{Base}/login-ticket?token={token}";
            if (!string.IsNullOrWhiteSpace(returnUrl))
            {
                url += $"&returnUrl={returnUrl}";
            }

            return url;
        }

        public const string Logout = $"{Base}/logout";
        public const string LogoutRedirect = $"{Base}/logout-redirect";
    }

    public static class Settings
    {
        private const string Base = "api/settings";

        public const string Ui = $"{Base}/ui";
        public const string Conversations = $"{Base}/conversations";
        public const string Privacy = $"{Base}/privacy";
    }

    public static class Notifications
    {
        public const string Base = "api/notifications";
        public const string MarkAllRead = $"{Base}/mark-all-read";
        public const string Clear = $"{Base}/clear";

        public static string MarkRead(string entryId, bool isSystemEntry) =>
            $"{Base}/mark-read/{Uri.EscapeDataString(entryId)}?system={isSystemEntry.ToString().ToLowerInvariant()}";
    }

    public static class NetworkSettings
    {
        public const string Configuration = "api/network-settings";
    }

    public static class ToolGovernance
    {
        public const string Summary = "api/tool-governance";

        public static string ByTool(string qualifiedName) =>
            $"{Summary}/{Uri.EscapeDataString(qualifiedName)}";
    }

    public static class PluginSettings
    {
        public const string Configuration = "api/plugin-settings";
    }

    public static class Credentials
    {
        public const string Base = "api/credentials";

        /// <summary>GET api/credentials/list — flat list of the user's non-managed credentials.</summary>
        public const string List = $"{Base}/list";

        public static string ByKey(string credentialKey) => $"{Base}/{Uri.EscapeDataString(credentialKey)}";
    }

    public static class Files
    {
        private const string Base = "api/files";

        public static string List(string? path) =>
            string.IsNullOrWhiteSpace(path)
                ? Base
                : $"{Base}?path={Uri.EscapeDataString(path)}";

        public const string Directories = $"{Base}/directories";
        public const string Documents = $"{Base}/documents";
        public const string Upload = $"{Base}/upload";
        public const string TextWrite = $"{Base}/text";
        public const string LibraryIndex = $"{Base}/library-index";
        public static string Delete(string path, bool isDirectory) =>
            $"{Base}?path={Uri.EscapeDataString(path)}&isDirectory={isDirectory.ToString().ToLowerInvariant()}";

        /// <summary>Selects the shared root on any Files route. Home is the API default.</summary>
        public static string WithRoot(string route, FileRoot root) =>
            root == FileRoot.Home ? route : $"{route}{(route.Contains('?') ? '&' : '?')}root={root}";

        public static string Download(string path) => $"{Base}/download?path={Uri.EscapeDataString(path)}";

        public static string Text(string path) => $"{Base}/text?path={Uri.EscapeDataString(path)}";

        public static string Backups(string path) =>
            $"{Base}/backups?path={Uri.EscapeDataString(path)}";

        public static string BackupText(string path, string backupId) =>
            $"{Base}/backups/text?path={Uri.EscapeDataString(path)}&backupId={Uri.EscapeDataString(backupId)}";

        public static string RestoreBackup(string path, string backupId) =>
            $"{Base}/backups/restore?path={Uri.EscapeDataString(path)}&backupId={Uri.EscapeDataString(backupId)}";
    }

    public static class Prompts
    {
        public const string Base = "api/prompts";

        public static string ByKey(string promptKey) => $"{Base}/{Uri.EscapeDataString(promptKey)}";
    }

    public static class Environment
    {
        private const string Base = "api/environment";

        public const string Export = $"{Base}/export";
        public const string BrowserExport = $"{Export}/browser";
        public const string Restore = $"{Base}/restore";
        public const string Reset = $"{Base}/reset";
        public static string ResetCompletion(string operationId) =>
            $"{Reset}/completion/{Uri.EscapeDataString(operationId)}";
        public const string RestoreFreshInstallRequiredProblemType =
            "urn:oscova:environment-restore:fresh-install-required";
        public const string RestorePendingProblemType =
            "urn:oscova:environment-restore:pending";
    }

    /// <summary>Provides application update discovery, execution, and completion routes.</summary>
    public static class ApplicationUpdate
    {
        private const string Base = "api/application-update";

        public const string Status = $"{Base}/status";
        public const string Start = $"{Base}/start";
        public static string Acknowledge(string operationId) => $"{Base}/acknowledge/{Uri.EscapeDataString(operationId)}";
    }

    /// <summary>Provides local Avatar installation and operation-status routes.</summary>
    public static class AvatarUpdate
    {
        public const string Status = "api/avatar-update/status";
        public const string Start = "api/avatar-update/start";
    }

    public static class Agents
    {
        private const string Base = "api/agents";

        public const string Configuration = Base;
        public const string Main = $"{Base}/main";
        public const string Custom = $"{Base}/custom";
        public const string Prebuilt = $"{Base}/prebuilt";

        /// <summary>Returns the install route for a bundled starter.</summary>
        public static string InstallPrebuilt(string id) => $"{Prebuilt}/{Uri.EscapeDataString(id)}/install";

        public static string CustomById(string agentId) => $"{Custom}/{Uri.EscapeDataString(agentId)}";
        public static string GoalLimit(string agentId) => $"{Base}/{Uri.EscapeDataString(agentId)}/goal-limit";
        public const string CustomImport = $"{Custom}/import";
        public static string CustomExport(string agentId) => $"{CustomById(agentId)}/export";
        public static string SharedPersonalContext(string agentId) => $"{Base}/shared/{Uri.EscapeDataString(agentId)}/personal-context";
        public static string InstructionPreview(string agentId) => $"{Base}/{Uri.EscapeDataString(agentId)}/instruction-preview";
        public static string Instruction(string agentId) => $"{Base}/{Uri.EscapeDataString(agentId)}/instruction";
    }

    public static class Dashboard
    {
        public const string Snapshot = "api/dashboard";
    }

    /// <summary>Provides achievement routes.</summary>
    public static class Achievements
    {
        public const string Summary = "api/achievements";
    }

    public static class EgressAudit
    {
        public const string Base = "api/egress-audit";

        public static string Query(
            string scope,
            int page,
            int pageSize,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            string? kind,
            string? source,
            string? searchTerm)
        {
            var parameters = new List<string>
            {
                $"scope={Uri.EscapeDataString(scope)}",
                $"page={page}",
                $"pageSize={pageSize}"
            };

            if (fromUtc is { } from)
            {
                parameters.Add($"fromUtc={Uri.EscapeDataString(from.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}");
            }

            if (toUtc is { } to)
            {
                parameters.Add($"toUtc={Uri.EscapeDataString(to.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}");
            }

            if (!string.IsNullOrWhiteSpace(kind))
            {
                parameters.Add($"kind={Uri.EscapeDataString(kind)}");
            }

            if (!string.IsNullOrWhiteSpace(source))
            {
                parameters.Add($"source={Uri.EscapeDataString(source)}");
            }

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                parameters.Add($"searchTerm={Uri.EscapeDataString(searchTerm)}");
            }

            return $"{Base}?{string.Join("&", parameters)}";
        }
    }

    public static class ModelProviders
    {
        private const string Base = "api/model-providers";

        public const string Configuration = Base;
        public const string Budgets = $"{Base}/budgets";
        public const string Test = $"{Base}/test";
        public const string AvailableModels = $"{Base}/available-models";
        public const string LocalModels = $"{Base}/local-models";
    }

    public static class Mcp
    {
        private const string Base = "api/mcp";

        public const string Configuration = Base;
        public const string Servers = $"{Base}/servers";
        public const string ReconnectAll = $"{Base}/reconnect-all";

        public static string ServerById(Guid serverId) => $"{Servers}/{serverId}";
        public static string Restart(Guid serverId) => $"{ServerById(serverId)}/restart";
    }

    public static class Admin
    {
        private const string Base = "api/admin";

        public const string Snapshot = Base;
        public const string AuthSettings = $"{Base}/auth-settings";
        public const string Users = $"{Base}/users";

        /// <summary>Gets the route for the selected account's execution permission grants.</summary>
        public static string UserPermissions(string userId) => $"{Users}/{Uri.EscapeDataString(userId)}/permissions";

        public static string UserProfile(string userId) => $"{Users}/{Uri.EscapeDataString(userId)}/profile";
        public static string UserRoles(string userId) => $"{Users}/{Uri.EscapeDataString(userId)}/roles";
        public static string UserPasswordReset(string userId) => $"{Users}/{Uri.EscapeDataString(userId)}/password-reset";
        public static string UserPin(string userId) => $"{Users}/{Uri.EscapeDataString(userId)}/pin";
        public static string UserById(string userId) => $"{Users}/{Uri.EscapeDataString(userId)}";
    }

    public static class OAuth
    {
        private const string Base = "api/oauth";
        private const string AdminBase = $"{Base}/admin/providers";

        public const string Status = $"{Base}/status";

        public static string Authorize(string provider) => $"{Base}/{Uri.EscapeDataString(provider)}/authorize";
        public static string Disconnect(string provider) => $"{Base}/{Uri.EscapeDataString(provider)}/disconnect";
        public const string AdminProviders = AdminBase;
        public static string AdminProvider(string provider) => $"{AdminBase}/{Uri.EscapeDataString(provider)}";
    }

    public static class Profile
    {
        public const string Base = "api/profile";
        public const string Vault = $"{Base}/vault";
        public const string VaultSetup = $"{Vault}/setup";
        public const string VaultUnlock = $"{Vault}/unlock";
        public const string VaultChange = $"{Vault}/change";
        public const string DuressCredentials = $"{Base}/duress-credentials";
    }

    /// <summary>Provides authenticated profile-picture routes with agent-visibility enforcement.</summary>
    public static class ProfilePictures
    {
        private const string Base = "api/profile-pictures";

        public const string User = $"{Base}/user";
        public const string Seeds = $"{Base}/seeds";

        public static string AgentById(string agentId) => $"{Base}/agent/{Uri.EscapeDataString(agentId)}";
        public static string SeedByName(string seedName) => $"{Seeds}/{Uri.EscapeDataString(seedName)}";
        public static string UserSeed(string seedName) => $"{User}/seed/{Uri.EscapeDataString(seedName)}";
        public static string AgentSeedById(string agentId, string seedName) => $"{AgentById(agentId)}/seed/{Uri.EscapeDataString(seedName)}";
    }

    /// <summary>Wallpaper API endpoints for the signed-in user and the packaged gallery.</summary>
    public static class Wallpapers
    {
        private const string Base = "api/wallpapers";

        public const string User = $"{Base}/user";
        public const string Seeds = $"{Base}/seeds";

        public static string SeedByName(string seedName) => $"{Seeds}/{Uri.EscapeDataString(seedName)}";
        public static string UserSeed(string seedName) => $"{User}/seed/{Uri.EscapeDataString(seedName)}";
    }

    public static class Tasks
    {
        public const string Base = "api/tasks";

        public static string ById(string taskId) => $"{Base}/{Uri.EscapeDataString(taskId)}";
        public static string Enabled(string taskId) => $"{ById(taskId)}/enabled";
        public static string Instruction(string taskId) => $"{ById(taskId)}/instruction";
        public static string Runs(string taskId, int? limit = null) =>
            limit.HasValue
                ? $"{ById(taskId)}/runs?limit={limit.Value}"
                : $"{ById(taskId)}/runs";
        public static string Run(string taskId, string runId) =>
            $"{ById(taskId)}/runs/{Uri.EscapeDataString(runId)}";
        public static string RunEgress(string taskId, string runId) =>
            $"{ById(taskId)}/runs/{Uri.EscapeDataString(runId)}/egress";
        public static string Trigger(string taskId) => $"{ById(taskId)}/trigger";
    }

    public static class Hooks
    {
        public const string Base = "api/hooks";

        public static string TaskRuns(string slug) => $"{Base}/tasks/{Uri.EscapeDataString(slug)}/runs";
        public static string TaskRunStatus(string slug, string runId) => $"{TaskRuns(slug)}/{Uri.EscapeDataString(runId)}";
    }

    public static class AdminHooks
    {
        public const string Base = "api/admin/hooks";
        public const string Settings = $"{Base}/settings";
        public const string ApiKeys = $"{Base}/api-keys";
        public const string SecretCredentials = $"{Base}/secret-credentials";

        public static string ApiKeyById(string id) => $"{ApiKeys}/{Uri.EscapeDataString(id)}";
        public static string SecretCredentialByKey(string credentialKey) => $"{SecretCredentials}/{Uri.EscapeDataString(credentialKey)}";
    }

    public static class Channels
    {
        public const string Base = "api/channels";
        public static string ById(string id) => $"{Base}/{Uri.EscapeDataString(id)}";
        public static string Status(string id) => $"{ById(id)}/status";
        public static string Start(string id) => $"{ById(id)}/start";
        public static string Stop(string id) => $"{ById(id)}/stop";
        public static string Apply(string id) => $"{ById(id)}/apply";
    }
    public static class Chat
    {
        private const string Base = "api/chat";
        public const string Threads = $"{Base}/threads";
        public const string EphemeralThreads = $"{Threads}/ephemeral";
        public const string RecentCurrentThread = $"{Threads}/recent-current";
        public const string ThreadSearch = $"{Threads}/search";

        /// <summary>Builds the conversation-search route for one query.</summary>
        public static string SearchThreads(string query) =>
            $"{ThreadSearch}?query={Uri.EscapeDataString(query)}";

        public static string Thread(string threadId) => $"{Threads}/{Uri.EscapeDataString(threadId)}";

        /// <summary>Builds a single-thread delete route without permitting collection-route normalization.</summary>
        public static string DeleteThread(string threadId)
        {
            // Empty and dot segments can normalize a single-thread route into the bulk-delete route.
            if (string.IsNullOrWhiteSpace(threadId) || threadId is "." or "..")
            {
                throw new ArgumentException("A nonempty thread ID other than '.' or '..' is required.", nameof(threadId));
            }

            return Thread(threadId);
        }

        /// <summary>Builds the route for closing a persisted question card.</summary>
        public static string DismissQuestion(string threadId, string messageId, string callId) =>
            $"{Thread(threadId)}/questions/{Uri.EscapeDataString(messageId)}/{Uri.EscapeDataString(callId)}/dismiss";

        /// <summary>Builds the route used to rename a chat thread.</summary>
        public static string ThreadTitle(string threadId) => $"{Thread(threadId)}/title";

        public static string BranchThread(string threadId, string messageId) =>
            $"{Thread(threadId)}/branch/{Uri.EscapeDataString(messageId)}";

        public static string SessionMessages(string threadId) => $"{Thread(threadId)}/session-messages";

        public static string SessionInspector(string threadId) => $"{Thread(threadId)}/session-inspector";

        public static string ContextPreflight(string threadId) => $"{Thread(threadId)}/context-preflight";

        /// <summary>Builds the manual-compaction route for one thread and optional provider.</summary>
        public static string Compact(string threadId, Guid? modelProviderId = null)
        {
            var route = $"{Thread(threadId)}/compact";
            return modelProviderId is null
                ? route
                : $"{route}?modelProviderId={Uri.EscapeDataString(modelProviderId.Value.ToString())}";
        }

        public static string Todos(string threadId, string? modelProviderId = null)
        {
            var route = $"{Thread(threadId)}/todos";
            return string.IsNullOrWhiteSpace(modelProviderId)
                ? route
                : $"{route}?modelProviderId={Uri.EscapeDataString(modelProviderId)}";
        }

        public static string AgentConversations(string threadId, string? modelProviderId = null)
        {
            var route = $"{Thread(threadId)}/agent-conversations";
            return string.IsNullOrWhiteSpace(modelProviderId)
                ? route
                : $"{route}?modelProviderId={Uri.EscapeDataString(modelProviderId)}";
        }

        /// <summary>Gets the detached run-start route for a chat thread.</summary>
        public static string Runs(string threadId) => $"{Thread(threadId)}/runs";

        /// <summary>Gets the conversation goal control route.</summary>
        public static string Goal(string threadId) => $"{Thread(threadId)}/goal";

        /// <summary>Gets the queued follow-ups route for a chat thread.</summary>
        public static string FollowUps(string threadId) => $"{Thread(threadId)}/follow-ups";
        /// <summary>Gets one queued follow-up's route.</summary>
        public static string FollowUp(string threadId, string messageId) => $"{FollowUps(threadId)}/{messageId}";
        /// <summary>Gets the route that transfers a queued follow-up into the running response.</summary>
        public static string SteerFollowUp(string threadId, string messageId) => $"{FollowUp(threadId, messageId)}/steer";

        public static string Stop(string threadId) => $"{Thread(threadId)}/stop";

        public static string Attachments(string threadId) => $"{Thread(threadId)}/attachments";

        public static string Attachment(string threadId, string attachmentId) =>
            $"{Attachments(threadId)}/{Uri.EscapeDataString(attachmentId)}";

        public static string ArtifactContent(string threadId, string artifactId) =>
            $"{Thread(threadId)}/artifacts/{Uri.EscapeDataString(artifactId)}/content";

        public static string ArtifactEdit(string threadId, string artifactId) =>
            $"{Thread(threadId)}/artifacts/{Uri.EscapeDataString(artifactId)}/edit";

        public static string ArtifactDownload(string threadId, string artifactId) =>
            $"{Thread(threadId)}/artifacts/{Uri.EscapeDataString(artifactId)}/download";

        public static string BooleanElicitation(Guid requestId) => $"{Base}/elicitations/{requestId}/boolean";

        public static string StringElicitation(Guid requestId) => $"{Base}/elicitations/{requestId}/string";

        public static string NumberElicitation(Guid requestId) => $"{Base}/elicitations/{requestId}/number";

        public static string EnumElicitation(Guid requestId) => $"{Base}/elicitations/{requestId}/enum";

        public static string ToolApproval(string requestId) => $"{Base}/tool-approvals/{Uri.EscapeDataString(requestId)}";
    }

    public static class Logs
    {
        public const string Base = "api/logs";

        public static string Files(string scope, string? targetUserId)
        {
            var query = ScopeQuery(scope, targetUserId);
            return $"{Base}/files?{query}";
        }

        public static string Page(
            string? fileName, string? level, string? searchTerm, int page, int pageSize,
            string scope, string? targetUserId)
        {
            var parameters = new List<string>
            {
                $"page={page}",
                $"pageSize={pageSize}",
                ScopeQuery(scope, targetUserId)
            };

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                parameters.Add($"fileName={Uri.EscapeDataString(fileName)}");
            }

            if (!string.IsNullOrWhiteSpace(level))
            {
                parameters.Add($"level={Uri.EscapeDataString(level)}");
            }

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                parameters.Add($"searchTerm={Uri.EscapeDataString(searchTerm)}");
            }

            return $"{Base}?{string.Join("&", parameters)}";
        }

        public static string Clear(string fileName, string scope, string? targetUserId) =>
            $"{Base}?fileName={Uri.EscapeDataString(fileName)}&{ScopeQuery(scope, targetUserId)}";

        public static string Export(
            string fileName, string format, string level, string? searchTerm,
            string scope, string? targetUserId)
        {
            var parameters = new List<string>
            {
                $"fileName={Uri.EscapeDataString(fileName)}",
                $"format={Uri.EscapeDataString(format)}",
                ScopeQuery(scope, targetUserId)
            };

            if (!string.IsNullOrWhiteSpace(level))
            {
                parameters.Add($"level={Uri.EscapeDataString(level)}");
            }

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                parameters.Add($"searchTerm={Uri.EscapeDataString(searchTerm)}");
            }

            return $"{Base}/export?{string.Join("&", parameters)}";
        }

        private static string ScopeQuery(string scope, string? targetUserId)
        {
            var query = $"scope={Uri.EscapeDataString(scope)}";
            if (!string.IsNullOrWhiteSpace(targetUserId))
            {
                query += $"&targetUserId={Uri.EscapeDataString(targetUserId)}";
            }

            return query;
        }
    }

    public static class ChatMemory
    {
        public const string Base = "api/chat-memory";

        public static string Page(string? agentId, string? threadId, int page, int pageSize)
        {
            var parameters = new List<string>
            {
                $"page={page}",
                $"pageSize={pageSize}"
            };

            if (!string.IsNullOrWhiteSpace(agentId))
            {
                parameters.Add($"agentId={Uri.EscapeDataString(agentId)}");
            }

            if (!string.IsNullOrWhiteSpace(threadId))
            {
                parameters.Add($"threadId={Uri.EscapeDataString(threadId)}");
            }

            return $"{Base}?{string.Join("&", parameters)}";
        }
    }

    public static class Hubs
    {
        public const string System = "/hubs/system";
    }

    /// <summary>Provides the AG-UI Server-Sent Events route for chat runs.</summary>
    public static class Agent
    {
        public const string Run = "/agent";
    }
}
