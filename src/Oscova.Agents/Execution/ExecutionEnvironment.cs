using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Tools.Shell;
using Oscova.Agents.Toolsets;
using Oscova.Agents.Workspace;

namespace Oscova.Agents.Execution;

/// <summary>Declares an execution device's shell facts and explicitly supported policies without starting a process.</summary>
public sealed record ExecutionEnvironmentDefinition(string Id, string Title, string Description,
    bool UsesHostPaths, bool SupportsOffline, bool SupportsNodeExecution, bool AllowsHostCommand,
    string Os, string Shell, string? TitleKey = null, string? DescriptionKey = null,
    string? HomeDirectory = null, string? TempDirectory = null);

/// <summary>Supplies the authorized inputs for one execution session.</summary>
public sealed record ExecutionSessionRequest(string ExecutionId, string AgentId, string WorkingDirectory,
    IReadOnlyList<AllowedDirectory> Directories, string? SkillsDirectory,
    IReadOnlyDictionary<string, string?> Environment, ShellPolicy Policy,
    bool Offline, bool AllowNetwork, int MaxOutputBytes, TimeSpan Timeout);

/// <summary>Supplies the device-local policy inputs used to check execution prerequisites.</summary>
public sealed record ExecutionAvailabilityRequest(bool AllowNetwork, IReadOnlyDictionary<string, string?> Environment);

/// <summary>Reports whether execution prerequisites are available and how to correct a detected failure.</summary>
public sealed record ExecutionAvailabilityResult(bool IsAvailable, string? Reason = null, string? ReasonKey = null);

/// <summary>Creates shell sessions on the device hosting this provider runtime.</summary>
public interface IExecutionEnvironmentProvider : IAsyncDisposable
{
    /// <summary>Checks this device's prerequisites without installing dependencies or executing user commands.</summary>
    Task<ExecutionAvailabilityResult> CheckAvailabilityAsync(ExecutionAvailabilityRequest request, CancellationToken ct);
    /// <summary>Creates an initialized session with the supplied mount, environment and command policy.</summary>
    Task<IExecutionSession> CreateSessionAsync(ExecutionSessionRequest request, CancellationToken ct);
    /// <summary>Stops and awaits shared preparation while the Host blocks admission, allowing later reuse.</summary>
    Task StopAsync();
}

/// <summary>Owns a foreground shell and the backend operations shared with background commands and scripts.</summary>
public interface IExecutionSession : IAsyncDisposable
{
    /// <summary>Gets the existing framework shell executor.</summary>
    ShellExecutor Executor { get; }
    /// <summary>Gets the shell used to shape script invocation.</summary>
    string? ShellBinary { get; }
    /// <summary>Maps an admitted file to a path visible to this session.</summary>
    string ResolveScriptPath(string hostPath);
    /// <summary>Repairs the foreground shell without stopping background jobs, returning false when the Host must replace the session.</summary>
    Task<bool> RecoverForegroundAsync(CancellationToken ct);
    /// <summary>Starts a backend command and streams its bounded output into the caller-owned buffer.</summary>
    Task<IExecutionBackgroundJob> StartBackgroundAsync(string command, BoundedOutputBuffer buffer, CancellationToken ct);
}

/// <summary>Reports and stops a backend job while its host retains public identity and ownership.</summary>
public interface IExecutionBackgroundJob : IAsyncDisposable
{
    /// <summary>Completes when the command ends, allowing ownership and retention cleanup without another tool call.</summary>
    Task Completion { get; }
    /// <summary>Gets whether the backend command has exited.</summary>
    bool HasExited { get; }
    /// <summary>Gets the reported exit code when available.</summary>
    int? ExitCode { get; }
    /// <summary>Refreshes remote output and completion state.</summary>
    Task RefreshAsync(CancellationToken ct);
    /// <summary>Stops the command and its children.</summary>
    Task StopAsync(CancellationToken ct);
}

/// <summary>Resolves admitted providers and their presentation without vendor-specific dispatch.</summary>
public interface IExecutionEnvironmentCatalog
{
    /// <summary>Gets currently admitted execution definitions.</summary>
    IReadOnlyList<ExecutionEnvironmentDefinition> Environments { get; }
    /// <summary>Resolves a registered definition or returns null for an unavailable selection.</summary>
    ExecutionEnvironmentDefinition? Find(string? id);
    /// <summary>Resolves an admitted runtime or fails without starting a host process.</summary>
    IExecutionEnvironmentProvider GetProvider(string id);
    /// <summary>Resolves the single admitted container launch capability.</summary>
    IContainerProcessLauncher GetContainerLauncher();
}

/// <summary>Supplies a transport-independent containerized process request.</summary>
public sealed record ContainerProcessRequest(string Name, string Command, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment, IReadOnlyList<AllowedDirectory> Directories, bool Offline);

/// <summary>Supplies standard process values to an owner that retains transport and process lifetime.</summary>
public sealed record ContainerProcessLaunch(string Command, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment, string? WorkingDirectory = null);

/// <summary>Prepares a shared container image and shapes a stdio process launch.</summary>
public interface IContainerProcessLauncher
{
    /// <summary>Gets whether this launcher can run on enrolled execution devices.</summary>
    bool SupportsNodeExecution { get; }
    /// <summary>Returns process values after the local image is ready.</summary>
    Task<ContainerProcessLaunch> PrepareAsync(ContainerProcessRequest request, CancellationToken ct);
}
