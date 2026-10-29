using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Core.Mcp;

public enum McpTransportType
{
    Http = 0,
    Stdio = 1,
}

public enum McpExecutionEnvironment
{
    /// <summary>
    /// Runs the stdio command as a host process. Standard MCP behavior.
    /// </summary>
    HostLocal = 0,

    /// <summary>
    /// Runs the stdio command through the admitted container execution provider.
    /// </summary>
    Containerized = 1,
}

public sealed partial class McpServerConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    public McpTransportType Transport { get; set; } = McpTransportType.Http;

    /// <summary>Declares how tools from this server use the network.</summary>
    public NetworkScope NetworkScope { get; set; } = NetworkScope.Incidental;

    // HTTP options
    public string? Endpoint { get; set; }

    public IDictionary<string, string>? Headers { get; set; }

    // STDIO options
    public string? Command { get; set; }

    /// <summary>
    /// Where the stdio command runs. Ignored for HTTP transport.
    /// </summary>
    public McpExecutionEnvironment ExecutionEnvironment { get; set; } = McpExecutionEnvironment.HostLocal;

    public IList<string>? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    public IDictionary<string, string?>? EnvironmentVariables { get; set; }

    /// <summary>Identifies the owner's device running this program. Null means the central server.</summary>
    public string? NodeId { get; set; }

    /// <summary>Allows the operator to offer this connection to regular accounts.</summary>
    public bool IsShared { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool IsValidForConnect()
    {
        if (!Enum.IsDefined(NetworkScope) || string.IsNullOrWhiteSpace(Name))
        {
            return false;
        }

        if (Transport == McpTransportType.Http)
        {
            // Registry installs save required headers blank or as {name} templates until the user supplies them.
            return !string.IsNullOrWhiteSpace(Endpoint) && Headers?.Values.Any(IsUnsetValue) != true;
        }

        return !string.IsNullOrWhiteSpace(Command);
    }

    /// <summary>True while a header or environment value is still blank or a {name} template.</summary>
    public bool HasUnsetValues() =>
        Headers?.Values.Any(IsUnsetValue) == true ||
        EnvironmentVariables?.Values.Any(value => value is not null && IsUnsetValue(value)) == true;

    private static bool IsUnsetValue(string value) => string.IsNullOrWhiteSpace(value) || TemplatePlaceholder().IsMatch(value);

    // A whole {name} token, so literal values such as "abc{def}ghi" stay valid.
    [GeneratedRegex(@"(?:^|\s)\{[A-Za-z_][A-Za-z0-9_]*\}(?:\s|$)", RegexOptions.None, 1000)]
    private static partial Regex TemplatePlaceholder();

    public McpServerConfig Clone()
    {
        return new McpServerConfig
        {
            Id = Id,
            IsShared = IsShared,
            IsEnabled = IsEnabled,
            NodeId = NodeId,
            Name = Name,
            Transport = Transport,
            NetworkScope = NetworkScope,
            Endpoint = Endpoint,
            Headers = Headers is null ? null : new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
            Command = Command,
            ExecutionEnvironment = ExecutionEnvironment,
            Arguments = Arguments is null ? null : new List<string>(Arguments),
            WorkingDirectory = WorkingDirectory,
            EnvironmentVariables = EnvironmentVariables is null ? null
                : new Dictionary<string, string?>(EnvironmentVariables, StringComparer.OrdinalIgnoreCase),
        };
    }
}
