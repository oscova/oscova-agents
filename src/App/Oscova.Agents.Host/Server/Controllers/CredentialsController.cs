using Oscova.Agents.Credentials;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.Credentials;
using Oscova.Agents.Core.Secrets;
using Oscova.Agents.Core.Secrets.Credentials;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Persistence;
using Oscova.Agents.Host.Services.UI;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/credentials")]
[IgnoreAntiforgeryToken]
public sealed class CredentialsController(
    UserServiceManager userServiceManager,
    ICredentialTypeRegistry credentialTypeRegistry,
    PluginPresentationService presentation,
    Oscova.Agents.Core.Auth.ExecutionPermissionRegistry executions)
    : UserScopeControllerBase(userServiceManager)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The projected Credentials page: declared user slots merged with saved rows, plus free-form rows.</summary>
    [HttpGet]
    public async Task<ActionResult<CredentialsContract>> Get(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var credentialService = userScope.CredentialService;

        // One query, then build both groups in memory — keeps the projection to a single round-trip.
        var rows = await credentialService.GetAllRowsAsync(ct);
        var rowsByKey = rows.ToDictionary(row => row.CredentialKey, StringComparer.OrdinalIgnoreCase);

        var view = new CredentialsContract();
        var declaredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var declaration in credentialTypeRegistry.All
                     .Where(declaration => declaration.Scope == CredentialScope.User))
        {
            declaredKeys.Add(declaration.Key);
            rowsByKey.TryGetValue(declaration.Key, out var row);
            view.Slots.Add(BuildDeclaredSlot(declaration, row));
        }

        foreach (var row in rows
                     .Where(row => !row.IsManaged && !declaredKeys.Contains(row.CredentialKey))
                     .OrderBy(row => row.Alias, StringComparer.OrdinalIgnoreCase))
        {
            view.FreeForm.Add(BuildFreeFormSlot(row));
        }

        return Ok(view);
    }

    /// <summary>Flat list of the user's non-managed credentials — backs the model/embedding provider dropdowns.</summary>
    [HttpGet("list")]
    public async Task<ActionResult<IReadOnlyList<CredentialListItem>>> GetList(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var credentials = await userScope.CredentialService.GetAllAsync(ct);
        return Ok(credentials.Where(credential => !credential.IsManaged).ToList());
    }

    /// <summary>One credential slot for the editor — a declared slot or a free-form row.</summary>
    [HttpGet("{credentialKey}")]
    public async Task<ActionResult<CredentialSlotContract>> GetByKey(string credentialKey, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var credentialService = userScope.CredentialService;

        var declaration = credentialTypeRegistry.Find(credentialKey);
        if (declaration is not null && declaration.Scope == CredentialScope.User)
        {
            var declaredRow = await credentialService.GetByKeyAsync(declaration.Key, ct);
            return Ok(BuildDeclaredSlot(declaration, declaredRow));
        }

        var row = await credentialService.GetByKeyAsync(credentialKey, ct);
        if (row?.IsManaged != false)
        {
            return NotFound();
        }

        return Ok(BuildFreeFormSlot(row));
    }

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SaveCredentialRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Alias))
        {
            return BadRequest("A credential name is required.");
        }

        var userScope = await GetUserScopeAsync(ct);
        var credentialService = userScope.CredentialService;

        if (!userScope.IsInRole("Admin") && request.Fields.Any(field => field.Source == CredentialFieldSource.Env))
        {
            return Forbid();
        }

        var requestedKey = request.CredentialKey?.Trim() ?? string.Empty;
        var declaration = requestedKey.Length == 0
            ? null
            : credentialTypeRegistry.Find(requestedKey);

        if (declaration is not null)
        {
            if (declaration.Scope != CredentialScope.User)
            {
                return BadRequest("This credential is managed by an administrator.");
            }

            var existingDeclaredRow = await credentialService.GetByKeyAsync(declaration.Key, ct);
            var declaredWrite = BuildDeclaredWrite(declaration, request);
            var declaredValidationError = ValidateWrite(declaredWrite, declaration.Fields, existingDeclaredRow);
            if (declaredValidationError is not null)
            {
                return BadRequest(declaredValidationError);
            }

            return await ChangeCredentialAsync(userScope, declaration.Key, request.ConfirmInterruptions, async () =>
            {
                try
                {
                    await credentialService.SaveAsync(declaredWrite, ct);
                }
                catch (ConfidentialDataLockedException)
                {
                    return StatusCode(
                        StatusCodes.Status423Locked,
                        "Create or unlock your Vault Password before saving stored secrets.");
                }

                InvalidateModelClientsIfReferenced(userScope, declaration.Key);
                return NoContent();
            }, ct);
        }

        // Free-form credential — its key is the slug of the alias.
        var slug = Slugs.ForCredentialAlias(request.Alias);
        if (slug.Length == 0)
        {
            return BadRequest("The credential name must contain at least one letter or digit.");
        }

        // Reject a free-form save whose slug would shadow a declared slot key.
        if (credentialTypeRegistry.Find(slug) is not null)
        {
            return Conflict($"The name '{request.Alias.Trim()}' is reserved for a built-in credential slot.");
        }

        var isNew = requestedKey.Length == 0;
        UserCredential? existingRow = null;
        if (!isNew)
        {
            existingRow = await credentialService.GetByKeyAsync(requestedKey, ct);
            if (existingRow?.IsManaged != false)
            {
                return NotFound();
            }
        }

        if (!string.Equals(requestedKey, slug, StringComparison.OrdinalIgnoreCase))
        {
            var collision = await credentialService.GetByKeyAsync(slug, ct);
            if (collision is not null)
            {
                return Conflict($"A credential named '{request.Alias.Trim()}' already exists.");
            }
        }

        var freeFormWrite = BuildFreeFormWrite(slug, request);
        var freeFormValidationError = ValidateWrite(freeFormWrite, CredentialFieldSets.FreeForm(), existingRow);
        if (freeFormValidationError is not null)
        {
            return BadRequest(freeFormValidationError);
        }

        return await ChangeCredentialAsync(userScope, isNew ? slug : requestedKey, request.ConfirmInterruptions, async () =>
        {
            var isRename = !isNew && !string.Equals(requestedKey, slug, StringComparison.OrdinalIgnoreCase);
            try
            {
                if (isRename)
                {
                    await PreserveRenamedStoredSecretAsync(credentialService, requestedKey, existingRow!, freeFormWrite, ct);
                }

                await credentialService.SaveAsync(freeFormWrite, ct);
            }
            catch (ConfidentialDataLockedException)
            {
                return StatusCode(
                    StatusCodes.Status423Locked,
                    "Create or unlock your Vault Password before saving stored secrets.");
            }

            // A renamed free-form credential moves its app-owned references before the old row is removed.
            if (isRename)
            {
                try
                {
                    _ = await CredentialReferenceService.RenameReferencesAsync(
                        userScope.ModelProviderSettings,
                        userScope.SpeechSettings,
                        requestedKey,
                        slug);
                }
                catch (CredentialReferenceRenameException ex)
                {
                    // A failed rollback may have persisted links to the staged slug, so retain both rows.
                    if (ex.ReferencesRestored)
                    {
                        await credentialService.DeleteAsync(slug, ct);
                    }

                    throw;
                }
                catch
                {
                    await credentialService.DeleteAsync(slug, ct);
                    throw;
                }

                await credentialService.DeleteAsync(requestedKey, ct);
            }

            InvalidateModelClientsIfReferenced(userScope, slug);
            return NoContent();
        }, ct);
    }

    /// <summary>Invalidates captured provider secrets through the shared execution-change lifecycle.</summary>
    private async Task<IActionResult> ChangeCredentialAsync(UserServiceScope scope, string key, bool confirmed,
        Func<Task<IActionResult>> apply, CancellationToken ct)
    {
        try
        {
            IActionResult result = NoContent();
            await executions.ChangeConfigurationAsync(
                () => scope.ModelProviderSettings.OwnedProviders
                    .Where(config => string.Equals(config.CredentialKey, key, StringComparison.OrdinalIgnoreCase))
                    .Select(config => Core.Llm.ModelProviderExecution.Key(config.Id)).ToArray(),
                async () => result = await apply(), confirmed, ct);
            return result;
        }
        catch (Oscova.Agents.Core.Auth.ExecutionPermissionRegistry.ConfirmationRequiredException ex)
        {
            return Conflict(new Oscova.Agents.Core.ApiContracts.Common.ErrorResult
            {
                Message = ex.Message,
                RequiresInterruptionConfirmation = true
            });
        }
    }

    private static void InvalidateModelClientsIfReferenced(UserServiceScope userScope, string credentialKey)
    {
        // Model clients capture resolved secrets at construction, so referenced writes must evict that cache.
        if (userScope.ModelProviderSettings.OwnedProviders.Any(config =>
            string.Equals(config.CredentialKey, credentialKey, StringComparison.OrdinalIgnoreCase)))
        {
            userScope.AppService.NotifyModelProviderSettingsChanged();
        }
    }

    [HttpDelete("{credentialKey}")]
    public async Task<IActionResult> Delete(string credentialKey, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var row = await userScope.CredentialService.GetByKeyAsync(credentialKey, ct);
        if (row?.IsManaged != false)
        {
            // Managed rows (OAuth tokens etc.) are owned by the system and not deletable from the UI.
            return NotFound();
        }

        // User credential deletes only block on settings that resolve through the user's credential service.
        var affectedSettings = CredentialReferenceService.GetUserReferences(
            userScope.ModelProviderSettings,
            userScope.SpeechSettings,
            credentialKey);
        if (affectedSettings.Count > 0)
        {
            return Conflict(new DeleteCredentialConflict
            {
                Message = $"This credential is still linked to {string.Join(", ", affectedSettings)}.",
                AffectedSettings = [.. affectedSettings]
            });
        }

        await userScope.CredentialService.DeleteAsync(credentialKey, ct);
        return NoContent();
    }

    private static CredentialWrite BuildDeclaredWrite(CredentialDeclaration declaration, SaveCredentialRequest request)
    {
        var fields = new List<CredentialFieldWrite>();
        foreach (var definition in declaration.Fields)
        {
            var submitted = request.Fields.FirstOrDefault(field =>
                string.Equals(field.Name, definition.Name, StringComparison.Ordinal));
            fields.Add(new CredentialFieldWrite
            {
                Name = definition.Name,
                IsSecret = definition.IsSecret,
                Source = submitted?.Source ?? CredentialFieldSource.Stored,
                Value = submitted?.Value,
                EnvVariable = submitted?.EnvVariable
            });
        }

        return new CredentialWrite
        {
            CredentialKey = declaration.Key,
            Alias = declaration.Label,
            Description = request.Description.NormalizeOptional() ?? declaration.Description,
            IsManaged = false,
            Fields = fields
        };
    }

    /// <summary>
    /// Validates a submitted field set while allowing a blank stored secret to retain its prior stored value.
    /// </summary>
    private static string? ValidateWrite(
        CredentialWrite write,
        IReadOnlyList<CredentialFieldDefinition> definitions,
        UserCredential? existingRow)
    {
        var existingFields = ParseData(existingRow);

        foreach (var definition in definitions)
        {
            var field = write.Fields.Single(submitted =>
                string.Equals(submitted.Name, definition.Name, StringComparison.Ordinal));

            if (field.Source is not CredentialFieldSource.Stored and not CredentialFieldSource.Env)
            {
                return $"Select a valid value source for {definition.Label}.";
            }

            if (field.Source == CredentialFieldSource.Env)
            {
                if (string.IsNullOrWhiteSpace(field.EnvVariable))
                {
                    return $"Enter an environment variable name for {definition.Label}.";
                }

                continue;
            }

            if (!definition.IsRequired || !string.IsNullOrWhiteSpace(field.Value))
            {
                continue;
            }

            var canPreserveStoredSecret = definition.IsSecret
                && existingFields.TryGetValue(definition.Name, out var existingField)
                && existingField.Source == CredentialFieldSource.Stored
                && !string.IsNullOrWhiteSpace(existingField.Value);

            if (!canPreserveStoredSecret)
            {
                return $"Enter a value for {definition.Label}.";
            }
        }

        return null;
    }

    /// <summary>
    /// Copies an unchanged stored free-form secret to the new key so renaming does not clear it.
    /// </summary>
    private static async Task PreserveRenamedStoredSecretAsync(
        ICredentialService credentialService,
        string oldCredentialKey,
        UserCredential existingRow,
        CredentialWrite write,
        CancellationToken ct)
    {
        var submitted = write.Fields.Single();
        if (submitted.Source == CredentialFieldSource.Env || !string.IsNullOrWhiteSpace(submitted.Value))
        {
            return;
        }

        var existingFields = ParseData(existingRow);
        if (!existingFields.TryGetValue(CredentialFieldSets.FreeFormFieldName, out var existingField) ||
            existingField.Source == CredentialFieldSource.Env ||
            string.IsNullOrWhiteSpace(existingField.Value))
        {
            return;
        }

        submitted.Value = await credentialService.ResolveFieldAsync(
            oldCredentialKey,
            CredentialFieldSets.FreeFormFieldName,
            ct) ?? throw new ConfidentialDataLockedException(
                "Unlock your Vault Password before renaming this stored credential.");
    }

    private static CredentialWrite BuildFreeFormWrite(string slug, SaveCredentialRequest request)
    {
        var submitted = request.Fields.FirstOrDefault(field =>
            string.Equals(field.Name, CredentialFieldSets.FreeFormFieldName, StringComparison.Ordinal));
        return new CredentialWrite
        {
            CredentialKey = slug,
            Alias = request.Alias.Trim(),
            Description = request.Description.NormalizeOptional(),
            IsManaged = false,
            Fields =
            [
                new CredentialFieldWrite
                {
                    Name = CredentialFieldSets.FreeFormFieldName,
                    IsSecret = true,
                    Source = submitted?.Source ?? CredentialFieldSource.Stored,
                    Value = submitted?.Value,
                    EnvVariable = submitted?.EnvVariable
                }
            ]
        };
    }

    private CredentialSlotContract BuildDeclaredSlot(CredentialDeclaration declaration, UserCredential? row)
    {
        var owner = credentialTypeRegistry.GetOwner(declaration.Key);
        var entries = ParseData(row);
        var slot = new CredentialSlotContract
        {
            Key = declaration.Key,
            Label = presentation.Text(owner, declaration.LabelKey, declaration.Label),
            Description = presentation.Text(owner, declaration.DescriptionKey, declaration.Description),
            Scope = declaration.Scope.ToString(),
            IsManaged = false,
            Alias = row?.Alias,
            RowExists = row is not null
        };

        foreach (var definition in declaration.Fields)
        {
            entries.TryGetValue(definition.Name, out var entry);
            slot.Fields.Add(new CredentialFieldContract
            {
                Name = definition.Name,
                Label = presentation.Text(owner, definition.LabelKey, definition.Label),
                IsSecret = definition.IsSecret,
                IsRequired = definition.IsRequired,
                DefaultEnvVariable = definition.DefaultEnvVariable,
                Source = entry?.Source ?? CredentialFieldSource.Stored,
                IsSet = HasFieldValue(entry),
                EnvVariable = entry?.Env
            });
        }

        return slot;
    }

    private static CredentialSlotContract BuildFreeFormSlot(UserCredential row)
    {
        var entries = ParseData(row);
        entries.TryGetValue(CredentialFieldSets.FreeFormFieldName, out var entry);
        return new CredentialSlotContract
        {
            Key = row.CredentialKey,
            Label = row.Alias,
            Description = row.Description ?? string.Empty,
            Scope = nameof(CredentialScope.User),
            IsManaged = row.IsManaged,
            Alias = row.Alias,
            RowExists = true,
            Fields =
            [
                new CredentialFieldContract
                {
                    Name = CredentialFieldSets.FreeFormFieldName,
                    Label = "Value",
                    IsSecret = true,
                    IsRequired = true,
                    Source = entry?.Source ?? CredentialFieldSource.Stored,
                    IsSet = HasFieldValue(entry),
                    EnvVariable = entry?.Env
                }
            ]
        };
    }

    private static Dictionary<string, CredentialFieldEntry> ParseData(UserCredential? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Data))
        {
            return new Dictionary<string, CredentialFieldEntry>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, CredentialFieldEntry>>(row.Data, JsonOptions)
                ?? new Dictionary<string, CredentialFieldEntry>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, CredentialFieldEntry>(StringComparer.Ordinal);
        }
    }

    private static bool HasFieldValue(CredentialFieldEntry? entry)
    {
        if (entry is null)
        {
            return false;
        }

        return entry.Source == CredentialFieldSource.Env
            ? !string.IsNullOrWhiteSpace(entry.Env)
            : !string.IsNullOrWhiteSpace(entry.Value);
    }

}
