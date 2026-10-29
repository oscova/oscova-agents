using Oscova.Agents.Core.Auth;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Nodes;
using Oscova.Agents.Core.Projects;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Skills;
using Oscova.Agents.Harness;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Utils;

#pragma warning disable MAAI001

namespace Oscova.Agents.Infrastructure.Skills;

/// <summary>
/// Bridges Microsoft Agent Framework file skills into the app-owned shell execution pipeline.
/// </summary>
public sealed class FileSkillScriptRunner(
    IUserDirectoryService userDirectoryService,
    RunContextAccessor runContextAccessor,
    IShellExecutorProvider executorProvider,
    IToolOutputStore outputStore,
    ExecutionPermissionRegistry executions,
    UserServiceManager users,
    NodeProjectExecutor nodes,
    INodePluginExecutor pluginNodes,
    SkillImportService packages)
{
    /// <summary>
    /// Exposes this runner in the delegate shape required by Microsoft Agent Framework file skills.
    /// </summary>
    public AgentFileSkillScriptRunner AsFrameworkRunner() => RunAsync;

    /// <summary>Runs a discovered skill script with the selected surface's live shell permission.</summary>
    public async Task<object?> RunAsync(AgentFileSkill skill, AgentFileSkillScript script,
        JsonElement? arguments, IServiceProvider? serviceProvider, CancellationToken cancellationToken)
    {
        var run = runContextAccessor.Current
            ?? throw new InvalidOperationException("Skill scripts require an active run context.");
        run.ConfigurationScope?.Track(executions, ExecutionSettings.ConfigurationKey,
            executions.GetConfigurationVersion(ExecutionSettings.ConfigurationKey));
        using var permission = executions.Register(run.UserId, UserPermissionKeys.ForShell(run.ProjectId is not null, run.AllowedDirectories),
            cancellationToken);
        var ct = permission.Token;
        if (run.ProjectId is { } projectId)
        {
            var scope = await users.GetOrCreateScopeAsync(run.UserId, ct).ConfigureAwait(false);
            var project = await scope.ProjectAccess.AuthorizeAsync(projectId, ct).ConfigureAwait(false);
            if (project.NodeId is not null)
            {
                pluginNodes.RequireExecutionReady(run.UserId, project.NodeId, run.ExecutionProviderId);
                if (scope.ExecutionSettings.ProviderId == null ||
                    scope.ExecutionSettings.ProviderId != (run.ConfiguredExecutionProviderId ?? run.ExecutionProviderId))
                {
                    throw new ToolFailureException("Shell execution is disabled or its execution environment changed. Start a new run after selecting the intended environment.");
                }

                return await RunOnNodeAsync(run, project, skill, script, arguments, ct).ConfigureAwait(false);
            }
        }

        ShellExecutor executor = await executorProvider.GetOrCreateAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        TryEnsureExecutableOnUnix(script.FullPath);
        var session = await executorProvider.GetSessionAsync(ct).ConfigureAwait(false);
        string scriptPath = session.ResolveScriptPath(script.FullPath);
        string? shellBinary = session.ShellBinary;
        string command = BuildCommand(scriptPath, ParseArguments(arguments), shellBinary);
        command = await executorProvider.PrepareCommandAsync(command, null, ct).ConfigureAwait(false);
        ShellResult result;
        try
        {
            result = await executor.RunAsync(command, ct).ConfigureAwait(false);
        }
        // The persistent executor needs recovery after cancellation before it can be reused.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await executorProvider.RecoverForegroundCommandAsync().ConfigureAwait(false);
            throw;
        }
        catch (ShellCommandRejectedException)
        {
            throw new ToolFailureException("Shell command was rejected by the execution policy.");
        }

        return ShellToolset.FormatResult(result, outputStore);
    }

    // Transfer only the invoking account's selected package, never a server directory or operator workspace.
    private async Task<object?> RunOnNodeAsync(RunContext run, ProjectDefinition project, AgentFileSkill skill,
        AgentFileSkillScript script, JsonElement? arguments, CancellationToken ct)
    {
        var name = skill.Frontmatter.Name;
        var root = Path.Combine(userDirectoryService.GetSkillsDirectoryPath(run.UserId), name);
        if (!PathHelper.ArePathsEqual(root, skill.Path) || !PathHelper.IsPathWithinDirectory(script.FullPath, root))
        {
            throw new UnauthorizedAccessException("The script must belong to the selected account's installed skill.");
        }

        var package = await packages.ExportPackageAsync(run.UserId, name, ct).ConfigureAwait(false);
        var result = await nodes.InvokeAsync(run.UserId, project, ProjectCapability.RunSkill,
            new { skillName = name, scriptPath = PathHelper.ToSlashSeparatedRelativePath(Path.GetRelativePath(root, script.FullPath)),
                arguments = ParseArguments(arguments), packageSize = package.Length },
            UserPermissionKeys.ForShell(project: true), run, ct, ReadChunkAsync).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.String ? result.GetString() : result;

        Task<JsonElement> ReadChunkAsync(string operation, JsonElement request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var offset = request.GetProperty("offset").GetInt32();
            if (operation != ProjectCapability.ReadSkillChunk || offset < 0 || offset >= package.Length)
            {
                throw new ArgumentException("The skill package chunk request is invalid.");
            }

            var length = Math.Min(ProjectCapability.SkillChunkBytes, package.Length - offset);
            return Task.FromResult(JsonSerializer.SerializeToElement(Convert.ToBase64String(package, offset, length)));
        }
    }

    internal static string BuildCommand(
        string scriptPath,
        IReadOnlyList<string> arguments,
        string? shellBinary = null)
    {
        string shellName = Path.GetFileNameWithoutExtension(shellBinary) ?? string.Empty;
        bool usePowerShellInvocation = shellName.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
                                       shellName.Equals("powershell", StringComparison.OrdinalIgnoreCase);
        bool useCmdInvocation = shellName.Equals("cmd", StringComparison.OrdinalIgnoreCase);
        Func<string, string> quote = usePowerShellInvocation
            ? QuotePowerShellPart
            : useCmdInvocation ? QuoteCmdPart : QuotePosixPart;
        var builder = new StringBuilder();
        if (usePowerShellInvocation)
        {
            builder.Append("& ");
        }

        builder.Append(quote(scriptPath));
        foreach (string argument in arguments)
        {
            builder.Append(' ')
                .Append(quote(argument));
        }

        return builder.ToString();
    }

    internal static IReadOnlyList<string> ParseArguments(JsonElement? arguments)
    {
        if (arguments is null)
        {
            return [];
        }

        return ParseArgumentElement(arguments.Value);
    }

    private static IReadOnlyList<string> ParseArgumentElement(JsonElement arguments)
    {
        if (arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (arguments.ValueKind == JsonValueKind.String)
        {
            string? rawArguments = arguments.GetString();
            if (string.IsNullOrWhiteSpace(rawArguments))
            {
                return [];
            }

            using JsonDocument document = ParseStringifiedArguments(rawArguments);
            return ParseArgumentElement(document.RootElement);
        }

        if (arguments.ValueKind == JsonValueKind.Object &&
            !arguments.EnumerateObject().Any())
        {
            return [];
        }

        if (arguments.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Skill script arguments must be a JSON array of strings.");
        }

        var values = new List<string>();
        foreach (JsonElement argument in arguments.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Skill script arguments must be strings.");
            }

            values.Add(argument.GetString() ?? string.Empty);
        }

        return values;
    }

    internal static void TryEnsureExecutableOnUnix(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode executableMode = mode |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute;
            if (executableMode != mode)
            {
                File.SetUnixFileMode(path, executableMode);
            }
        }
        catch
        {
            // The shell result will still surface permission or filesystem failures.
        }
    }

    private static JsonDocument ParseStringifiedArguments(string rawArguments)
    {
        try
        {
            return JsonDocument.Parse(rawArguments);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Skill script arguments must be a JSON array of strings.", exception);
        }
    }

    private static string QuotePosixPart(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string QuotePowerShellPart(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string QuoteCmdPart(string value)
    {
        if (value.Any(char.IsWhiteSpace) || value.ContainsAny('"', '%', '!'))
        {
            throw new InvalidOperationException(
                "Skill script paths and arguments containing whitespace, quotes, or variable markers require PowerShell on Windows.");
        }

        return value
            .Replace("^", "^^", StringComparison.Ordinal)
            .Replace("&", "^&", StringComparison.Ordinal)
            .Replace("|", "^|", StringComparison.Ordinal)
            .Replace("<", "^<", StringComparison.Ordinal)
            .Replace(">", "^>", StringComparison.Ordinal)
            .Replace("(", "^(", StringComparison.Ordinal)
            .Replace(")", "^)", StringComparison.Ordinal);
    }
}
