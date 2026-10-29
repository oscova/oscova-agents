using Oscova.Agents.Conversations;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;
using Oscova.Agents.Core.Projects;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace Oscova.Agents.Host.Artifacts;

/// <summary>
/// Projects file mutations and local file references from persisted assistant replies into thread artifacts.
/// </summary>
public static class ThreadArtifactProjector
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly string[][] PageRoutes = [.. typeof(ThreadArtifactProjector).Assembly.GetTypes()
        .SelectMany(type => type.GetCustomAttributes<RouteAttribute>())
        .Select(route => route.Template.Split('/', StringSplitOptions.RemoveEmptyEntries))];

    /// <summary>Projects file mutations and references separately for workspace and project destinations.</summary>
    public static ThreadArtifactProjection Project(IEnumerable<ChatDisplayMessage> messages, bool projectSelected)
    {
        var workspace = new Dictionary<ArtifactKey, ArtifactBuilder>(ArtifactKeyComparer.Instance);
        var project = new Dictionary<ArtifactKey, ArtifactBuilder>();
        var sequence = 0;
        foreach (var message in messages)
        {
            if (message.From != MessageOwner.Bot) { continue; }

            foreach (var segment in message.Segments)
            {
                var projectEnvironment = projectSelected;
                var environment = segment.FunctionCall?.AdditionalProperties?.TryGetValue(
                    ProjectToolRouter.FileEnvironmentMetadataKey, out var owner) == true ? owner?.ToString() : null;
                if (environment is null && TryGetString(segment, "environment", out var selectedEnvironment))
                {
                    environment = selectedEnvironment;
                }

                if (environment is not null)
                {
                    if (environment == "workspace") { projectEnvironment = false; }
                    else if (environment == "project") { projectEnvironment = true; }
                }

                ApplySegment(message.AgentId, segment, projectEnvironment ? project : workspace, ref sequence);
                if (segment.Type == ChatSegmentType.Text && !string.IsNullOrWhiteSpace(segment.Text))
                {
                    foreach (var link in Markdown.Parse(segment.Text).Descendants<LinkInline>())
                    {
                        var current = workspace.Values.Select(builder => builder.ToArtifact("workspace"))
                            .Concat(project.Values.Select(builder => builder.ToArtifact("project")));
                        if (ResolveReference(link.Url, message.AgentId, current, projectSelected) is not { } reference) { continue; }

                        Upsert(reference.Environment == "project" ? project : workspace, message.AgentId,
                            reference.AgentPath, segment.ProcessedAt ?? segment.CreatedAt, ref sequence);
                    }
                }
            }
        }

        var artifacts = workspace.Values.Select(builder => builder.ToArtifact("workspace"))
            .Concat(project.Values.Select(builder => builder.ToArtifact("project")));
        return new ThreadArtifactProjection(artifacts.OrderByDescending(artifact => artifact.UpdatedAt)
            .ThenByDescending(artifact => artifact.Sequence).ToArray());
    }

    /// <summary>Matches local Markdown references to their producing agent and existing file identity.</summary>
    public static ThreadArtifact? ResolveReference(string? url, string? agentId,
        IEnumerable<ThreadArtifact> artifacts, bool projectSelected)
    {
        // Links to app pages such as /tasks navigate in place instead of resolving as files.
        if (string.IsNullOrWhiteSpace(url) || IsPageLink(url.Trim())) { return null; }

        var path = Uri.UnescapeDataString(url.Trim());
        if (path.StartsWith('#') || path.StartsWith("//", StringComparison.Ordinal)) { return null; }

        if (!IsDriveQualified(path) && Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) { return null; }

            path = uri.LocalPath;
        }

        path = NormalizeAgentPath(path);
        if (path.Length == 0) { return null; }

        var environment = projectSelected && !path.StartsWith("/workspace/", StringComparison.Ordinal) &&
            !path.StartsWith(PathHelper.SelfAgentPath + "/", StringComparison.Ordinal) &&
            !path.StartsWith("/private/", StringComparison.Ordinal)
            ? "project" : "workspace";
        var candidates = artifacts.Where(artifact => string.Equals(artifact.AgentId, agentId, StringComparison.Ordinal));
        var exact = candidates.Where(artifact => PathComparer.Equals(artifact.AgentPath, path) ||
                PathComparer.Equals(artifact.CanonicalPath, path))
            .OrderByDescending(artifact => artifact.Environment == environment).FirstOrDefault();
        if (exact is not null) { return exact; }
        // A filename in a reply can refer to a fully qualified path from the preceding tool result.
        if (!path.StartsWith('/') && !IsDriveQualified(path))
        {
            var matches = candidates.Where(artifact => artifact.AgentPath.EndsWith("/" + path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length == 1) { return matches[0]; }
        }

        return new ArtifactBuilder(agentId, path).ToArtifact(environment);
    }

    private static void ApplySegment(
        string? agentId,
        ChatSegment segment,
        IDictionary<ArtifactKey, ArtifactBuilder> artifacts,
        ref int sequence)
    {
        if (segment.Type != ChatSegmentType.ToolCall ||
            segment.FunctionCall is null ||
            segment.ExecutionState != ToolExecutionState.Completed)
        {
            return;
        }

        var (_, toolName) = ToolNaming.SplitQualifiedName(segment.FunctionCall.Name);
        var result = GetResultText(segment.FunctionResult?.Result);
        var updatedAt = segment.ProcessedAt ?? segment.CreatedAt;
        var canonicalPath = segment.FunctionCall.AdditionalProperties?.TryGetValue(
            ProjectToolRouter.FilePathMetadataKey, out var resolvedPath) == true ? resolvedPath?.ToString() : null;

        switch (toolName)
        {
            case "write" when result.StartsWith("Wrote ", StringComparison.Ordinal) ||
                                   result.StartsWith("Appended ", StringComparison.Ordinal):
                if (TryGetString(segment, "file_path", out var writePath))
                {
                    Upsert(artifacts, agentId, writePath, updatedAt, ref sequence, canonicalPath);
                }

                break;
            case "edit" when result.StartsWith("Replaced ", StringComparison.Ordinal):
                if (TryGetString(segment, "file_path", out var editPath))
                {
                    Upsert(artifacts, agentId, editPath, updatedAt, ref sequence, canonicalPath);
                }

                break;
            case "apply_patch":
                ApplyPatch(agentId, segment, artifacts, updatedAt, ref sequence, canonicalPath);
                break;
        }
    }

    private static void ApplyPatch(
        string? agentId,
        ChatSegment segment,
        IDictionary<ArtifactKey, ArtifactBuilder> artifacts,
        DateTimeOffset updatedAt,
        ref int sequence,
        string? canonicalBaseDirectory)
    {
        if (!TryGetString(segment, "patch", out var patch) ||
            !TryGetString(segment, "base_directory", out var baseDirectory))
        {
            return;
        }

        PatchDocument document;
        try
        {
            document = PatchParser.Parse(patch);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return;
        }

        foreach (var operation in document.Operations)
        {
            var sourcePath = CombineAgentPath(baseDirectory, operation.Path);
            var canonicalPath = canonicalBaseDirectory is null ? null : CombineAgentPath(canonicalBaseDirectory, operation.Path);
            switch (operation)
            {
                case AddFileOperation:
                    Upsert(artifacts, agentId, sourcePath, updatedAt, ref sequence, canonicalPath);
                    break;
                case DeleteFileOperation:
                    artifacts.Remove(CreateArtifactKey(agentId, sourcePath));
                    break;
                case UpdateFileOperation { MoveTo: { } moveTo }:
                    var destinationPath = CombineAgentPath(baseDirectory, moveTo);
                    Move(artifacts, agentId, sourcePath, destinationPath, updatedAt, ref sequence,
                        canonicalBaseDirectory is null ? null : CombineAgentPath(canonicalBaseDirectory, moveTo));
                    break;
                case UpdateFileOperation:
                    Upsert(artifacts, agentId, sourcePath, updatedAt, ref sequence, canonicalPath);
                    break;
            }
        }
    }

    private static void Upsert(
        IDictionary<ArtifactKey, ArtifactBuilder> artifacts,
        string? agentId,
        string path,
        DateTimeOffset updatedAt,
        ref int sequence,
        string? canonicalPath = null)
    {
        var normalizedPath = NormalizeAgentPath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        var key = CreateArtifactKey(agentId, normalizedPath);
        if (!artifacts.TryGetValue(key, out var artifact))
        {
            artifact = new ArtifactBuilder(agentId, normalizedPath);
            artifacts[key] = artifact;
        }

        artifact.UpdatedAt = updatedAt;
        artifact.Sequence = sequence++;
        if (canonicalPath is not null) { artifact.CanonicalPath = NormalizeAgentPath(canonicalPath); }
    }

    private static void Move(
        IDictionary<ArtifactKey, ArtifactBuilder> artifacts,
        string? agentId,
        string sourcePath,
        string destinationPath,
        DateTimeOffset updatedAt,
        ref int sequence,
        string? canonicalPath)
    {
        var normalizedSource = NormalizeAgentPath(sourcePath);
        var normalizedDestination = NormalizeAgentPath(destinationPath);
        if (string.IsNullOrWhiteSpace(normalizedDestination))
        {
            return;
        }

        var sourceKey = CreateArtifactKey(agentId, normalizedSource);
        var destinationKey = CreateArtifactKey(agentId, normalizedDestination);
        artifacts.Remove(sourceKey, out var artifact);
        artifacts.Remove(destinationKey, out var replacedArtifact);
        artifact ??= replacedArtifact ?? new ArtifactBuilder(agentId, normalizedDestination);
        artifact.AgentPath = normalizedDestination;
        artifact.CanonicalPath = canonicalPath is null ? null : NormalizeAgentPath(canonicalPath);
        artifact.UpdatedAt = updatedAt;
        artifact.Sequence = sequence++;
        artifacts[destinationKey] = artifact;
    }

    private static bool TryGetString(ChatSegment segment, string key, out string value)
    {
        value = string.Empty;
        if (segment.FunctionCall?.Arguments is null ||
            !segment.FunctionCall.Arguments.TryGetValue(key, out var raw) ||
            raw is null)
        {
            return false;
        }

        value = raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement element => element.GetRawText(),
            _ => raw.ToString() ?? string.Empty
        };
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string GetResultText(object? result)
    {
        var text = result switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement element => element.GetRawText(),
            _ => result?.ToString() ?? string.Empty
        };

        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            return text;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(text) ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static string CombineAgentPath(string baseDirectory, string path)
    {
        if (path.StartsWith('/') || IsDriveQualified(path))
        {
            return NormalizeAgentPath(path);
        }

        return NormalizeAgentPath($"{baseDirectory.TrimEnd('/', '\\')}/{path}");
    }

    private static string NormalizeAgentPath(string path)
    {
        var normalized = PathHelper.ToSlashSeparatedRelativePath(path.Trim());
        var uncRooted = normalized.StartsWith("//", StringComparison.Ordinal);
        var rooted = normalized.StartsWith('/');
        var drivePrefix = IsDriveQualified(normalized) ? normalized[..2] : string.Empty;
        var contentStart = uncRooted ? 2 : rooted ? 1 : drivePrefix.Length;
        var segments = normalized[contentStart..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();

        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == ".." && stack.Count > 0 && stack[^1] != "..")
            {
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(segment);
        }

        var prefix = uncRooted ? "//" : rooted ? "/" : drivePrefix;
        if (drivePrefix.Length > 0 && stack.Count > 0)
        {
            prefix += "/";
        }

        return prefix + string.Join('/', stack);
    }

    private static bool IsPageLink(string url)
    {
        if (!url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal)) { return false; }

        var segments = url.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return PageRoutes.Any(route => route.Length == segments.Length && route.Zip(segments).All(pair =>
            pair.First.StartsWith('{') || string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsDriveQualified(string path) =>
        path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';

    private static ArtifactKey CreateArtifactKey(string? agentId, string normalizedPath) =>
        new(agentId ?? string.Empty, normalizedPath);

    private static string CreateArtifactId(string? agentId, string normalizedPath, string environment)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{environment}\n{agentId}\n{normalizedPath}"));
        return Convert.ToHexStringLower(hash);
    }

    private sealed class ArtifactBuilder(string? agentId, string agentPath)
    {
        public string? AgentId { get; } = agentId;
        public string AgentPath { get; set; } = agentPath;
        public string? CanonicalPath { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int Sequence { get; set; }

        public ThreadArtifact ToArtifact(string environment)
        {
            var fileName = AgentPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? AgentPath;
            return new ThreadArtifact(CreateArtifactId(AgentId, AgentPath, environment), AgentId, AgentPath,
                fileName, UpdatedAt, ArtifactFilePresentation.GetPreviewKind(fileName), Sequence, environment, CanonicalPath);
        }
    }

    private readonly record struct ArtifactKey(string AgentId, string AgentPath);

    private sealed class ArtifactKeyComparer : IEqualityComparer<ArtifactKey>
    {
        public static ArtifactKeyComparer Instance { get; } = new();

        public bool Equals(ArtifactKey x, ArtifactKey y)
        {
            return string.Equals(x.AgentId, y.AgentId, StringComparison.Ordinal) &&
                PathComparer.Equals(x.AgentPath, y.AgentPath);
        }

        public int GetHashCode(ArtifactKey obj)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.AgentId),
                PathComparer.GetHashCode(obj.AgentPath));
        }
    }
}

/// <summary>
/// Contains the current files attributed to a thread.
/// </summary>
public sealed record ThreadArtifactProjection(IReadOnlyList<ThreadArtifact> Artifacts);

/// <summary>Represents a file output and its execution environment within its owning thread.</summary>
public sealed record ThreadArtifact(
    string Id,
    string? AgentId,
    string AgentPath,
    string FileName,
    DateTimeOffset UpdatedAt,
    ArtifactPreviewKind PreviewKind,
    int Sequence,
    string Environment,
    string? CanonicalPath = null);
