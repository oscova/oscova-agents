using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Oscova.Agents.Core.ApiContracts.Hooks;
using Oscova.Agents.Core.ApiContracts.Credentials;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Persistence;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/hooks")]
[IgnoreAntiforgeryToken]
public sealed class AdminHooksController(
    HookSettings hookSettings,
    IHookApiKeyService apiKeyService,
    ISystemCredentialService systemCredentialService,
    ILogger<AdminHooksController> logger) : ControllerBase
{
    private const string HookSecretCredentialPrefix = "hook-";
    private const string HookSecretFieldName = "value";

    [HttpGet("settings")]
    public async Task<ActionResult<HookSettingsSummary>> GetSettings(CancellationToken ct)
    {
        var hooks = await hookSettings.RunExclusiveAsync(async () =>
        {
            await hookSettings.LoadAsync();
            return hookSettings.Hooks.ToList();
        }, ct);
        return Ok(new HookSettingsSummary
        {
            Hooks = hooks,
            ApiKeys = MapApiKeySummaries(await apiKeyService.ListAsync(ct))
        });
    }

    [HttpPut("settings")]
    public async Task<ActionResult<HookSettingsSummary>> SaveSettings([FromBody] UpdateHookSettingsRequest request, CancellationToken ct)
    {
        try
        {
            await hookSettings.RunExclusiveAsync(async () =>
            {
                await hookSettings.LoadAsync();
                hookSettings.ReplaceHooks(request.Hooks ?? []);
                await hookSettings.SaveAsync();
            }, ct);
            return Ok(new HookSettingsSummary
            {
                Hooks = [.. hookSettings.Hooks],
                ApiKeys = MapApiKeySummaries(await apiKeyService.ListAsync(ct))
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("api-keys")]
    public async Task<ActionResult<IReadOnlyList<HookApiKeySummary>>> ListApiKeys(CancellationToken ct)
    {
        return Ok(MapApiKeySummaries(await apiKeyService.ListAsync(ct)));
    }

    [HttpPost("api-keys")]
    public async Task<ActionResult<CreatedHookApiKey>> CreateApiKey([FromBody] CreateHookApiKeyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("API key name is required.");
        }

        try
        {
            var created = await apiKeyService.CreateAsync(new HookApiKeyCreateOptions
            {
                Name = request.Name,
                Scopes = request.Scopes ?? [],
                AllowedTaskSlugs = request.AllowedTaskSlugs ?? []
            }, ct);

            return Ok(MapCreatedApiKey(created));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("api-keys/{id}")]
    public async Task<IActionResult> DeleteApiKey(string id, CancellationToken ct)
    {
        return await apiKeyService.DeleteAsync(id, ct) ? NoContent() : NotFound();
    }

    [HttpPatch("api-keys/{id}")]
    public async Task<IActionResult> SetApiKeyEnabled(string id, [FromBody] UpdateHookApiKeyRequest request, CancellationToken ct)
    {
        return await apiKeyService.SetEnabledAsync(id, request.IsEnabled, ct) ? NoContent() : NotFound();
    }

    [HttpGet("secret-credentials")]
    public async Task<ActionResult<IReadOnlyList<HookSecretCredentialSummary>>> ListSecretCredentials(CancellationToken ct)
    {
        var credentials = await systemCredentialService.GetAllAsync(ct);
        return Ok(credentials
            .Where(IsHookSecretCredential)
            .Select(MapSecretCredential)
            .ToList());
    }

    [HttpPut("secret-credentials/{credentialKey}")]
    public async Task<IActionResult> SaveSecretCredential(string credentialKey, [FromBody] SaveHookSecretCredentialRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Alias))
        {
            return BadRequest("A hook secret name is required.");
        }

        var normalizedCredentialKey = NormalizeHookSecretCredentialKey(credentialKey, request.Alias);
        if (string.IsNullOrWhiteSpace(normalizedCredentialKey))
        {
            return BadRequest("The hook secret name must contain at least one letter or digit.");
        }

        var existing = await systemCredentialService.GetByKeyAsync(normalizedCredentialKey, ct);
        if (existing is null && string.IsNullOrWhiteSpace(request.SecretValue))
        {
            return BadRequest("A secret value is required for new hook secret credentials.");
        }

        try
        {
            var protectionUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (protectionUserId is null)
            {
                return Unauthorized();
            }

            await systemCredentialService.SaveAsync(
                protectionUserId,
                new CredentialWrite
                {
                    CredentialKey = normalizedCredentialKey,
                    Alias = request.Alias,
                    Description = request.Description,
                    IsManaged = false,
                    Fields =
                    [
                        new CredentialFieldWrite
                        {
                            Name = HookSecretFieldName,
                            IsSecret = true,
                            Source = CredentialFieldSource.Stored,
                            Value = request.SecretValue
                        }
                    ]
                },
                ct);

            return NoContent();
        }
        catch (ConfidentialDataLockedException)
        {
            return StatusCode(
                StatusCodes.Status423Locked,
                "Create or unlock your Vault Password before saving stored secrets.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save hook secret credential {CredentialKey}.", normalizedCredentialKey);
            return Problem("Hook secret credential could not be saved.");
        }
    }

    [HttpDelete("secret-credentials/{credentialKey}")]
    public async Task<IActionResult> DeleteSecretCredential(string credentialKey, CancellationToken ct)
    {
        var normalizedKey = NormalizeHookSecretCredentialKey(credentialKey, credentialKey);
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return BadRequest("A hook secret credential key is required.");
        }

        var existing = await systemCredentialService.GetByKeyAsync(normalizedKey, ct);
        if (existing is null || !IsHookSecretCredential(existing.CredentialKey))
        {
            return NotFound();
        }

        var affectedSettings = await hookSettings.RunExclusiveAsync(async () =>
        {
            await hookSettings.LoadAsync();
            return CredentialReferenceService.GetHookReferences(hookSettings, normalizedKey);
        }, ct);
        if (affectedSettings.Count > 0)
        {
            return Conflict(new DeleteCredentialConflict
            {
                Message = $"This credential is still linked to {string.Join(", ", affectedSettings)}.",
                AffectedSettings = [.. affectedSettings]
            });
        }

        try
        {
            await systemCredentialService.DeleteAsync(normalizedKey, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete hook secret credential {CredentialKey}.", normalizedKey);
            return Problem("Hook secret credential could not be deleted.");
        }
    }

    private static CreatedHookApiKey MapCreatedApiKey(HookApiKeyCreationResult result)
    {
        return new CreatedHookApiKey
        {
            Key = MapApiKeySummary(result.Key),
            PlaintextToken = result.PlaintextToken
        };
    }

    private static List<HookApiKeySummary> MapApiKeySummaries(IReadOnlyList<HookApiKeyMetadata> keys)
    {
        return keys.Select(MapApiKeySummary).ToList();
    }

    private static HookApiKeySummary MapApiKeySummary(HookApiKeyMetadata key)
    {
        return new HookApiKeySummary
        {
            Id = key.Id,
            Name = key.Name,
            IsEnabled = key.IsEnabled,
            TokenPrefix = key.TokenPrefix,
            Scopes = [.. key.Scopes],
            AllowedTaskSlugs = [.. key.AllowedTaskSlugs],
            CreatedAt = key.CreatedAt,
            LastUsedAt = key.LastUsedAt
        };
    }

    private static HookSecretCredentialSummary MapSecretCredential(CredentialListItem credential)
    {
        return new HookSecretCredentialSummary
        {
            CredentialKey = credential.CredentialKey,
            Alias = credential.Alias,
            Description = credential.Description,
            IsSet = credential.IsSet,
            CreatedAt = credential.CreatedAt,
            UpdatedAt = credential.UpdatedAt
        };
    }

    private static string NormalizeHookSecretCredentialKey(string? requestedKey, string alias)
    {
        var source = string.IsNullOrWhiteSpace(requestedKey) ? alias : requestedKey;
        var slug = Slugs.ForCredentialAlias(source ?? string.Empty);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return string.Empty;
        }

        return slug.StartsWith(HookSecretCredentialPrefix, StringComparison.OrdinalIgnoreCase)
            ? slug
            : $"{HookSecretCredentialPrefix}{slug}";
    }

    private static bool IsHookSecretCredential(CredentialListItem credential)
    {
        return IsHookSecretCredential(credential.CredentialKey);
    }

    private static bool IsHookSecretCredential(string credentialKey)
    {
        return credentialKey.StartsWith(HookSecretCredentialPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
