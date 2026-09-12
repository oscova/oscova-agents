using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Tools.Shell;
using Oscova.Agents.Harness;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Toolsets;

/// <summary>Exposes one approval-required-by-default foreground command on a caller-provided host executor.</summary>
/// <param name="createExecutor">Creates a new executor that the tool owns and disposes after each call.</param>
[DisplayName("Host Shell")]
public sealed class HostShellToolset(
    Func<CancellationToken, Task<ShellExecutor>> createExecutor) : ICrewToolset
{
    /// <summary>Gets the model-visible host command name.</summary>
    public const string ToolName = "exec_host_command";

    /// <summary>Identifies the built-in host command used by policy filters.</summary>
    public const string QualifiedToolName = "host_shell__exec_host_command";

    /// <summary>Runs one foreground command directly on the host. Approval is required by default.</summary>
    [DisplayName(ToolName)]
    [Description("Runs one foreground command directly on the host outside Docker from the matching workspace. Use relative paths. Use this for host-persistent global installs or host-only tools; use exec_command for normal workspace work. Requires user approval by default.")]
    [ToolPolicy(ApprovalMode = ToolApprovalMode.RequireApproval)]
    [ToolNetworkScope(NetworkScope.Incidental)]
    public async Task<object> ExecHostCommandAsync(
        [Description("The host command to execute from the current workspace")] string command,
        [Description("Optional. Maximum seconds before termination. Default 120, maximum 600.")] int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ToolFailureException("A host shell command is required.");
        }
        await using ShellExecutor executor = await createExecutor(cancellationToken).ConfigureAwait(false);
        return await ShellToolset.ExecuteCommandAsync(
            executor,
            // The await using teardown is this one-shot executor's timeout and cancellation recovery.
            static () => Task.CompletedTask,
            command,
            timeoutSeconds,
            cancellationToken).ConfigureAwait(false);
    }
}
