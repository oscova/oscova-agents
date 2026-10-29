using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Mcp.Registry;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Mcp.Registry;

namespace Oscova.Agents.Host.Server.Controllers;

/// <summary>
/// Authenticated HTTP surface for discovering servers from the official MCP Registry.
/// </summary>
[ApiController]
[Authorize]
[Route("api/mcp-registry")]
public sealed class McpRegistryController(
    IMcpRegistryService registry,
    ILogger<McpRegistryController> logger) : ControllerBase
{
    // Open to any authenticated user: discovery is impersonal and the registry read is unauthenticated.
    /// <summary>
    /// Searches the official MCP Registry and returns editor-ready install options.
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<McpRegistrySearchResponse>> Search(
        [FromQuery] string? q,
        CancellationToken cancellationToken)
    {
        try
        {
            var servers = await registry.SearchAsync(q ?? string.Empty, cancellationToken);
            return Ok(new McpRegistrySearchResponse
            {
                Entries = [.. servers.Select(McpRegistryServerMapper.Map)]
            });
        }
        catch (McpRegistryException ex)
        {
            logger.LogWarning(ex, "MCP registry search failed.");
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new McpRegistrySearchResponse { Error = ex.Message });
        }
    }
}
