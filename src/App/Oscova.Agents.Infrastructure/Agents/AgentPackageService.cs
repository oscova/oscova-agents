using Oscova.Agents.Secrets;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.ApiContracts.Agents;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Users;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Definitions;
using Oscova.Agents.Persistence;
using Oscova.Agents.Policies;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Infrastructure.Agents;

/// <summary>Exports and imports portable custom-agent packages.</summary>
public sealed class AgentPackageService(
    IAgentDefinitionRepository agents,
    IAgentCatalog catalog,
    IProfilePictureService profilePictures,
    YamlConverter yaml,
    Oscova.Agents.Secrets.IContentProtector protector)
{
    public const string FileExtension = ".oscagent";
    public const long MaxUploadSizeBytes = 5 * ByteSize.OneMegabyte;

    private const int MaxTextEntryBytes = 1024 * 1024;

    /// <summary>Builds an Oscova agent package for the supplied persisted agent.</summary>
    public async Task<byte[]> ExportAsync(
        AgentDefinition agent,
        string appVersion,
        string? defaultInstruction,
        CancellationToken ct = default)
    {
        var agentDirectory = catalog.TryGetAgentDirectory(agent.Id)
            ?? throw new InvalidOperationException($"Unknown agentId '{agent.Id}'.");
        var agentPath = Path.Combine(agentDirectory, UserFileNames.AgentFile);
        var owner = catalog.TryGetOwnerUserId(agent.Id)
            ?? throw new InvalidOperationException("The agent owner is unavailable.");
        var source = await EncryptedFileContent.ReadAllTextAsync(owner, agentPath, protector, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Agent definition not found.", agentPath);
        var (frontmatter, body) = MarkdownFrontmatter.Split(source);
        yaml.RequireSchemaVersion(frontmatter, AgentDefinitionFile.CurrentSchemaVersion);
        var file = yaml.Deserialize<AgentDefinitionFile>(frontmatter);
        if (file.InstructionSource is not null)
        {
            body = InstructionSelection.Select(file.InstructionMode, body, defaultInstruction);
        }

        file.InstructionSource = null;
        file.InstructionMode = null;
        file.DefaultInstruction = null;

        file.Id = string.Empty;
        file.OwnerUserId = string.Empty;
        file.AllowedUserIds = [];
        file.IsMain = false;
        file.IsShared = false;
        file.AllowedDirectories = [];

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new AgentPackageManifest
            {
                FormatVersion = AgentPackageManifest.CurrentFormatVersion,
                AppVersion = appVersion,
                ExportedAtUtc = DateTimeOffset.UtcNow,
                AgentName = agent.Name
            };
            await WriteTextEntryAsync(
                archive,
                AgentPackageManifest.EntryName,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                ct).ConfigureAwait(false);
            await WriteTextEntryAsync(
                archive,
                ArchiveAgentFile,
                MarkdownFrontmatter.Combine(yaml.Serialize(file), body),
                ct).ConfigureAwait(false);
            var policyPath = Path.Combine(agentDirectory, UserFileNames.AgentToolPoliciesFile);
            var policies = await EncryptedFileContent.ReadAllTextAsync(owner, policyPath, protector, ct).ConfigureAwait(false);
            if (policies is not null)
            {
                await WriteTextEntryAsync(archive, ArchiveToolPoliciesFile, policies, ct).ConfigureAwait(false);
            }

            await CopyFileEntryAsync(
                archive,
                Path.Combine(agentDirectory, UserFileNames.ProfileImageFile),
                UserFileNames.ProfileImageFile,
                ct).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    /// <summary>Imports an Oscova agent package as a new agent owned by the supplied user.</summary>
    public async Task<ImportedAgentResult> ImportAsync(
        string userId,
        Stream package,
        string? requestedName,
        IReadOnlySet<string> availableToolNames,
        IReadOnlySet<string> availableWorkspaceNames,
        Func<string, bool> isModelProviderAvailable,
        CancellationToken ct = default)
    {
        // ZipArchive read mode needs a seekable stream, so keep this request-sized package in memory.
        using var packageBuffer = new MemoryStream();
        await package.CopyToAsync(packageBuffer, ct).ConfigureAwait(false);
        packageBuffer.Position = 0;

        using var archive = new ZipArchive(packageBuffer, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry(AgentPackageManifest.EntryName)
            ?? throw new ArgumentException("The file is not an agent package.");
        var manifestText = await ReadTextEntryAsync(manifestEntry, ct).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<AgentPackageManifest>(manifestText)
            ?? throw new ArgumentException("The file is not an agent package.");
        if (manifest.FormatVersion != AgentPackageManifest.CurrentFormatVersion)
        {
            throw new ArgumentException("This agent package format is unsupported. Export it again with a compatible application.");
        }

        var agentEntry = archive.GetEntry(ArchiveAgentFile)
            ?? throw new ArgumentException("The file is not an agent package.");
        var agentText = await ReadTextEntryAsync(agentEntry, ct).ConfigureAwait(false);
        var (frontmatter, systemInstruction) = MarkdownFrontmatter.Split(agentText);
        yaml.RequireSchemaVersion(frontmatter, AgentDefinitionFile.CurrentSchemaVersion);
        var file = yaml.Deserialize<AgentDefinitionFile>(frontmatter);
        var allowedWorkspaces = file.AllowedWorkspaces
            .Where(availableWorkspaceNames.Contains)
            .ToList();
        var agent = new AgentDefinition
        {
            OwnerUserId = userId,
            Name = await ResolveNameAsync(userId, requestedName, manifest.AgentName, file.Name, ct)
                .ConfigureAwait(false),
            Description = file.Description,
            SystemInstruction = systemInstruction,
            ModelOverrideId = file.ModelOverrideId,
            UseCallerModelForConsultations = file.UseCallerModelForConsultations,
            IsCompactionEnabled = file.IsCompactionEnabled,
            IsMemoryEnabled = file.IsMemoryEnabled,
            IsTodoEnabled = file.IsTodoEnabled,
            IsSubagentDelegationEnabled = file.IsSubagentDelegationEnabled,
            IsLocalOnly = file.IsLocalOnly,
            IsExposedAsTool = file.IsExposedAsTool,
            IsToolSearchEnabled = file.IsToolSearchEnabled,
            AllowedToolNames = [.. file.AllowedToolNames],
            PinnedToolNames = [.. file.PinnedToolNames],
            AllowedSkillNames = [.. file.AllowedSkillNames],
            AllowedWorkspaces = allowedWorkspaces,
            ToolPolicies = await ReadToolPoliciesAsync(archive, ct).ConfigureAwait(false)
        };

        var removedReferenceCount = file.AgentToolIds.Count;
        removedReferenceCount += file.AllowedWorkspaces.Count - agent.AllowedWorkspaces.Count;
        if (!string.IsNullOrWhiteSpace(agent.ModelOverrideId) &&
            !isModelProviderAvailable(agent.ModelOverrideId))
        {
            agent.ModelOverrideId = null;
            removedReferenceCount++;
        }

        var sanitized = ToolReferenceSanitizer.RemoveUnavailableTools(
            agent.AllowedToolNames,
            agent.PinnedToolNames,
            agent.ToolPolicies,
            availableToolNames);
        removedReferenceCount += sanitized.RemovedAllowedToolNames.Count;

        // Validate every package entry before the imported agent becomes durable.
        var profileImage = await ReadProfileImageAsync(archive, ct).ConfigureAwait(false);

        await agents.SaveAgentAsync(userId, agent, ct).ConfigureAwait(false);
        if (profileImage is not null)
        {
            using var stream = new MemoryStream(profileImage, writable: false);
            await profilePictures.WriteAgentPictureAsync(agent.Id, stream, ct).ConfigureAwait(false);
        }

        return new ImportedAgentResult
        {
            AgentId = agent.Id,
            Name = agent.Name,
            RemovedReferenceCount = removedReferenceCount
        };
    }

    /// <summary>Builds the download filename for an exported agent.</summary>
    public static string BuildFileName(string agentName) => Slugs.ForAgentFolder(agentName) + FileExtension;

    private async Task<string> ResolveNameAsync(
        string userId,
        string? requestedName,
        string manifestName,
        string fileName,
        CancellationToken ct)
    {
        var baseName = !string.IsNullOrWhiteSpace(requestedName)
            ? requestedName
            : !string.IsNullOrWhiteSpace(manifestName)
                ? manifestName
                : !string.IsNullOrWhiteSpace(fileName)
                    ? fileName
                    : "Imported Agent";
        var existing = await agents.GetAllAgentsAsync(userId, ct).ConfigureAwait(false);
        return AgentCallableName.MakeUniqueDisplayName(
            baseName,
            existing.Where(agent => !agent.IsMain).Select(agent => agent.Name));
    }

    private async Task<List<AgentToolPolicy>> ReadToolPoliciesAsync(
        ZipArchive archive,
        CancellationToken ct)
    {
        var entry = archive.GetEntry(ArchiveToolPoliciesFile);
        if (entry is null)
        {
            return [];
        }

        var content = await ReadTextEntryAsync(entry, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var file = yaml.Deserialize<AgentToolPoliciesFile>(content);
        return [.. file.Tools
            .Where(policy => !string.IsNullOrWhiteSpace(policy.ToolName))
            .Select(policy => new AgentToolPolicy
            {
                ToolName = policy.ToolName,
                ApprovalMode = policy.ApprovalMode
            })];
    }

    private static async Task<byte[]?> ReadProfileImageAsync(
        ZipArchive archive,
        CancellationToken ct)
    {
        var entry = archive.GetEntry(UserFileNames.ProfileImageFile);
        if (entry is null)
        {
            return null;
        }

        var image = await ReadEntryAsync(entry, ProfilePictureContent.MaxUploadSizeBytes, ct)
            .ConfigureAwait(false);
        if (!ImageContent.IsSupportedImageHeader(image.AsSpan(0, Math.Min(image.Length, 12))))
        {
            return null;
        }

        return image;
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        long maxBytes,
        CancellationToken ct)
    {
        if (entry.Length > maxBytes)
        {
            throw new ArgumentException($"Entry '{entry.FullName}' is too large.");
        }

        await using var source = entry.Open();
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new ArgumentException($"Entry '{entry.FullName}' is too large.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    private static async Task<string> ReadTextEntryAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        var bytes = await ReadEntryAsync(entry, MaxTextEntryBytes, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string entryName,
        string content,
        CancellationToken ct)
    {
        await using var stream = archive.CreateEntry(entryName).Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct).ConfigureAwait(false);
    }

    private static async Task CopyFileEntryAsync(
        ZipArchive archive,
        string filePath,
        string entryName,
        CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        await using var source = File.OpenRead(filePath);
        await using var destination = archive.CreateEntry(entryName).Open();
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    private sealed class AgentPackageManifest
    {
        public const int CurrentFormatVersion = 1;
        public const string EntryName = "manifest.json";

        public int FormatVersion { get; set; }
        public string AppVersion { get; set; } = string.Empty;
        public DateTimeOffset ExportedAtUtc { get; set; }
        public string AgentName { get; set; } = string.Empty;
    }
    private const string ArchiveAgentFile = "agent.md";
    private const string ArchiveToolPoliciesFile = "tool-policies.yaml";
}
