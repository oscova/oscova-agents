using System.Text.Json.Serialization;

namespace Oscova.Agents.Core.Mcp.Registry;

/// <summary>
/// Raw MCP Registry server shape from GET /v0.1/servers.
/// </summary>
public sealed record McpRegistryServer(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("websiteUrl")] string? WebsiteUrl,
    [property: JsonPropertyName("repository")] McpRegistryRepository? Repository,
    [property: JsonPropertyName("packages")] IReadOnlyList<McpRegistryPackage>? Packages,
    [property: JsonPropertyName("remotes")] IReadOnlyList<McpRegistryRemote>? Remotes);

/// <summary>
/// Source repository metadata published by the MCP Registry.
/// </summary>
public sealed record McpRegistryRepository(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("source")] string? Source);

/// <summary>
/// Local package install metadata published by the MCP Registry.
/// </summary>
public sealed record McpRegistryPackage(
    [property: JsonPropertyName("registryType")] string? RegistryType,
    [property: JsonPropertyName("identifier")] string? Identifier,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("runtimeHint")] string? RuntimeHint,
    [property: JsonPropertyName("runtimeArguments")] IReadOnlyList<McpRegistryArgument>? RuntimeArguments,
    [property: JsonPropertyName("packageArguments")] IReadOnlyList<McpRegistryArgument>? PackageArguments,
    [property: JsonPropertyName("environmentVariables")] IReadOnlyList<McpRegistryKeyValueInput>? EnvironmentVariables,
    [property: JsonPropertyName("transport")] McpRegistryTransport? Transport);

/// <summary>
/// Remote HTTP/SSE connection metadata published by the MCP Registry.
/// </summary>
public sealed record McpRegistryRemote(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("headers")] IReadOnlyList<McpRegistryKeyValueInput>? Headers);

/// <summary>
/// Transport metadata attached to an MCP Registry package.
/// </summary>
public sealed record McpRegistryTransport(
    [property: JsonPropertyName("type")] string? Type);

/// <summary>
/// Header or environment variable input metadata published by the MCP Registry.
/// </summary>
public sealed record McpRegistryKeyValueInput(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("default")] string? Default,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("isSecret")] bool IsSecret,
    [property: JsonPropertyName("isRequired")] bool IsRequired);

/// <summary>
/// Command-line argument metadata published by the MCP Registry.
/// </summary>
public sealed record McpRegistryArgument(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("valueHint")] string? ValueHint);
