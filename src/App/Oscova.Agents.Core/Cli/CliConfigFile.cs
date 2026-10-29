namespace Oscova.Agents.Core.Cli;

/// <summary>
/// Per-OS-user CLI preferences loaded before command execution.
/// </summary>
public sealed class CliConfigFile
{
    public string? Host { get; set; }
    public string? DefaultAgentId { get; set; }
    public bool? DefaultStream { get; set; }
}