using System.Collections.ObjectModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Oscova.Agents.Core.ApiContracts.ModelProviders;
using Oscova.Agents.Llm;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Infrastructure.Utils;
using Oscova.Agents.Core.Auth;
using Oscova.Agents.Core.Llm;
using Oscova.Agents.Secrets;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Infrastructure.Metrics;

namespace Oscova.Agents.Host.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/model-providers")]
[IgnoreAntiforgeryToken]
public sealed class ModelProvidersController(
    UserServiceManager userServiceManager,
    IModelProviderCatalog modelProviderCatalog,
    ExecutionPermissionRegistry executions,
    IContentProtector contentProtector,
    IUserPermissionService permissions,
    ModelBudgetService budgets,
    ILogger<ModelProvidersController> logger)
    : UserScopeControllerBase(userServiceManager)
{
    [HttpGet]
    public async Task<ActionResult<ModelProviderConfiguration>> Get(CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var settings = userScope.ModelProviderSettings;
        var canManage = (await permissions.GetGrantedAsync(userScope.UserId, ct)).Contains(UserPermissionKeys.ManagePersonalModelProviders);

        return Ok(new ModelProviderConfiguration
        {
            CanManageCatalog = canManage,
            SharedProviderIds = settings.SharedProviders.Select(provider => provider.Id).ToArray(),
            Budgets = settings.Budgets,
            OperatorDefaultChatModelProviderId = settings.OperatorDefaultChatModelProviderId,
            OperatorDefaultEmbeddingModelProviderId = settings.OperatorDefaultEmbeddingModelProviderId,
            EmbeddingProviderIds = settings.EmbeddingProviderIds.ToArray(),
            // Consumer selectors need model metadata, never credential links or private endpoint paths.
            ModelProviders = [.. settings.ModelProviderConfigs
                .Where(config => canManage || settings.OwnerOf(config.Id) != settings)
                .Select(config => canManage && settings.OwnerOf(config.Id) == settings ? config.Clone() : new ModelProviderConfig
            {
                Id = config.Id,
                Name = config.Name,
                Type = config.Type,
                ModelId = config.ModelId,
                SupportsImages = config.SupportsImages,
                SupportsThinking = config.SupportsThinking,
                ContextWindowSize = config.ContextWindowSize,
                VectorDimensions = config.VectorDimensions
            })],
            SelectedChatModelProviderId = settings.ChatProviderOverrideId,
            EmbeddingModelProviderId = settings.EmbeddingProviderOverrideId,
            FallbackChatModelProviderId = settings.FallbackProviderOverrideId,
            LocalModels = canManage ? GetLocalModels() : [],
            AvailableProviderTypes = [.. modelProviderCatalog.All
                .Select(provider => provider.Type)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(type => type, StringComparer.Ordinal)],
            ProviderTypeDefaults = [.. modelProviderCatalog.All
                .GroupBy(provider => provider.Type, StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(provider => new ProviderTypeDefault(
                    provider.Type,
                    provider.DefaultEndpoint,
                    provider.DefaultModel,
                    provider is IModelDownloadProvider))
                .OrderBy(providerDefault => providerDefault.Key, StringComparer.Ordinal)],
            SupportedEmbeddingTypes = [.. modelProviderCatalog.All
                .Where(provider => provider.SupportsEmbedding)
                .Select(provider => provider.Type)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(type => type, StringComparer.Ordinal)]
        });
    }

    /// <summary>Returns budget estimates for definitions visible to this account.</summary>
    [HttpGet("budgets")]
    public async Task<ActionResult<IReadOnlyList<ModelBudgetStatus>>> GetBudgets(CancellationToken ct)
    {
        var scope = await GetUserScopeAsync(ct);
        var canManage = (await permissions.GetGrantedAsync(scope.UserId, ct)).Contains(UserPermissionKeys.ManagePersonalModelProviders);
        var result = new List<ModelBudgetStatus>();
        foreach (var provider in scope.ModelProviderSettings.ModelProviderConfigs)
        {
            if (canManage || scope.ModelProviderSettings.OwnerOf(provider.Id) != scope.ModelProviderSettings)
            {
                result.Add((await budgets.GetAsync(scope.UserId, provider.Id, ct)).ForViewer());
            }
        }

        return Ok(result);
    }

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] ModelProviderConfiguration request, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var settings = userScope.ModelProviderSettings;
        var isOperator = userScope.IsInRole("Admin");
        var canManage = (await permissions.GetGrantedAsync(userScope.UserId, ct)).Contains(UserPermissionKeys.ManagePersonalModelProviders);
        if (!canManage && (request.ModelProviders.Count != 0 || request.EmbeddingProviderIds.Count != 0 || request.Budgets.Count != 0))
        {
            return Forbid();
        }

        // A consumer can submit only personal definitions, never replace an operator entry by its ID.
        if (request.ModelProviders.Any(provider => settings.SharedProviders.Any(shared => shared.Id == provider.Id)))
        {
            return Forbid();
        }

        var ownedProviders = canManage ? request.ModelProviders.Select(config => config.Clone()).ToArray() : [];
        foreach (var config in ownedProviders)
        {
            var previous = settings.OwnedProviders.FirstOrDefault(item => item.Id == config.Id);
            if (previous is not null && previous.Type != config.Type) { config.Options = null; }

            foreach (var error in ModelProviderValidation.Validate(config, modelProviderCatalog.Find(config.Type), previous))
            {
                ModelState.AddModelError($"ModelProviders[{config.Id}].{error.Key}", error.Value);
            }
        }

        if (!ModelState.IsValid) { return ValidationProblem(ModelState); }

        if (request.Budgets.Any(pair => !ownedProviders.Any(provider => provider.Id == pair.Key) ||
            pair.Value.DailyLimitUsd is <= 0 || pair.Value.MonthlyLimitUsd is <= 0))
        {
            return BadRequest("Budget limits must be positive or blank and belong to a personal provider.");
        }

        var ownedEmbeddingIds = canManage ? request.EmbeddingProviderIds.ToHashSet() : [];
        if (ownedEmbeddingIds.Any(id => !ownedProviders.Any(provider => provider.Id == id)))
        {
            return BadRequest("Embedding entries must belong to the submitted personal catalog.");
        }

        var providers = settings.SharedProviders.Concat(ownedProviders).ToArray();
        var embeddingIds = settings.EmbeddingProviderIds.Except(settings.OwnedEmbeddingProviderIds).Concat(ownedEmbeddingIds).ToHashSet();
        var chatIds = providers.Where(config => !embeddingIds.Contains(config.Id)).Select(config => config.Id).ToHashSet();
        if (providers.Select(config => config.Id).Distinct().Count() != providers.Length ||
            embeddingIds.Any(id => !providers.Any(config => config.Id == id && modelProviderCatalog.Find(config.Type)?.SupportsEmbedding == true)))
        {
            return BadRequest("Provider IDs must be unique, and every embedding entry must identify a configured embedding-capable provider.");
        }

        if (!CanKeepSelection(request.SelectedChatModelProviderId, settings.ChatProviderOverrideId, chatIds) ||
            !CanKeepSelection(request.EmbeddingModelProviderId, settings.EmbeddingProviderOverrideId, embeddingIds) ||
            !CanKeepSelection(request.FallbackChatModelProviderId, settings.FallbackProviderOverrideId, chatIds))
        {
            return BadRequest("Choose a provider available for the selected purpose.");
        }

        var effectiveChatId = request.SelectedChatModelProviderId ?? settings.OperatorDefaultChatModelProviderId;
        if (request.FallbackChatModelProviderId.HasValue && request.FallbackChatModelProviderId == effectiveChatId)
        {
            return BadRequest("The fallback provider must differ from the selected chat provider.");
        }

        IReadOnlyList<string> ChangedKeys() => canManage ? settings.OwnedProviders
            .Where(current => !ModelProviderExecution.Equivalent(current, ownedProviders.FirstOrDefault(next => next.Id == current.Id)) ||
                settings.EmbeddingProviderIds.Contains(current.Id) != embeddingIds.Contains(current.Id))
            .Select(current => ModelProviderExecution.Key(current.Id)).ToArray() : [];

        var consumers = isOperator
            ? UserServices.ActiveUserIds.Select(id => UserServices.TryGetExistingScope(id, out var scope) ? scope : null)
                .Where(scope => scope?.ModelProviderSettings.IsLoaded == true && contentProtector.IsAvailable(scope.UserId))
                .Select(scope => scope!).ToArray()
            : [userScope];

        try
        {
            await executions.ChangeConfigurationAsync(ChangedKeys, async () =>
            {
                var previousEmbeddings = consumers.ToDictionary(scope => scope.UserId,
                    scope => scope.ModelProviderSettings.SelectedEmbeddingModelProvider?.Clone(), StringComparer.Ordinal);
                var previousSettings = settings.ToYaml();
                try
                {
                    if (canManage)
                    {
                        settings.ModelProviderConfigs = new ObservableCollection<ModelProviderConfig>(ownedProviders);
                        settings.Budgets = new Dictionary<Guid, ModelBudgetPolicy>(request.Budgets);
                        settings.EmbeddingProviderIds = ownedEmbeddingIds.ToArray();
                    }

                    settings.SelectedChatModelProviderId = request.SelectedChatModelProviderId;
                    settings.EmbeddingModelProviderId = request.EmbeddingModelProviderId;
                    settings.FallbackChatModelProviderId = request.FallbackChatModelProviderId;
                    await settings.SaveAsync();
                }
                catch
                {
                    settings.FromYaml(previousSettings);
                    throw;
                }

                foreach (var consumer in consumers)
                {
                    consumer.AppService.NotifyModelProviderSettingsChanged();
                    if (!EmbeddingIndexIdentityChanged(previousEmbeddings[consumer.UserId], consumer.ModelProviderSettings.SelectedEmbeddingModelProvider))
                    {
                        continue;
                    }

                    try
                    {
                        await consumer.ChatMemoryService.ResetIndexAsync(consumer.UserId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Embedding indexes require reconciliation after provider settings changed for user {UserId}.", consumer.UserId);
                        throw new InvalidOperationException("Provider settings were saved, but an embedding index could not be refreshed. Review the server error and retry the index rebuild.", ex);
                    }
                }
            }, request.ConfirmInterruptions, ct);
        }
        catch (ExecutionPermissionRegistry.ConfirmationRequiredException ex)
        {
            return Conflict(ex.Message);
        }

        foreach (var consumer in consumers)
        {
            // A changed cap can make previously paused Library files eligible again.
            await consumer.LibraryIndexingService.QueueReconciliationAsync();
            try { await UserServices.NotifyUnavailableProvidersAsync(consumer, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Provider settings were saved, but attention notifications could not be refreshed for {UserId}.", consumer.UserId);
            }
        }

        return NoContent();
    }

    /// <summary>Preserves an existing unavailable choice while rejecting a newly selected unknown provider.</summary>
    private static bool CanKeepSelection(Guid? requested, Guid? existing, IReadOnlySet<Guid> available) =>
        requested is null || requested == existing || available.Contains(requested.Value);

    [HttpPost("test")]
    public async Task<ActionResult<ProviderTestResult>> Test([FromBody] ModelProviderConfig request, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        var canManage = (await permissions.GetGrantedAsync(userScope.UserId, ct)).Contains(UserPermissionKeys.ManagePersonalModelProviders);
        if (!canManage || userScope.ModelProviderSettings.OwnerOf(request.Id) != userScope.ModelProviderSettings)
        {
            // Availability probes use the approved catalog, never a consumer-supplied endpoint or credential.
            var configured = userScope.ModelProviderSettings.ModelProviderConfigs.FirstOrDefault(provider => provider.Id == request.Id);
            if (configured is null) { return Forbid(); }

            request = configured;
        }

        foreach (var error in ModelProviderValidation.Validate(request, modelProviderCatalog.Find(request.Type)))
        {
            ModelState.AddModelError(error.Key, error.Value);
        }

        if (!ModelState.IsValid) { return ValidationProblem(ModelState); }

        var result = await userScope.AppService.TestProviderAsync(request, ct);

        return Ok(result);
    }

    [HttpPost("available-models")]
    public async Task<ActionResult<AvailableModelsResult>> GetAvailableModels([FromBody] ModelProviderConfig request, CancellationToken ct)
    {
        var userScope = await GetUserScopeAsync(ct);
        await permissions.RequireAsync(userScope.UserId, UserPermissionKeys.ManagePersonalModelProviders, ct);
        if (userScope.ModelProviderSettings.OwnerOf(request.Id) != userScope.ModelProviderSettings) { return Forbid(); }

        return Ok(await userScope.AppService.GetAvailableModelsAsync(request, cancellationToken: ct));
    }

    [HttpGet("local-models")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetLocalModelsEndpoint(CancellationToken ct)
    {
        // Personal provider editors use the same installed models as the operator.
        if (!(await permissions.GetGrantedAsync(GetUserId(), ct)).Contains(UserPermissionKeys.ManagePersonalModelProviders))
        {
            return Forbid();
        }

        return Ok(GetLocalModels());
    }

    private static List<string> GetLocalModels()
    {
        if (!Directory.Exists(PathUtil.GgufModelsPath))
        {
            return [];
        }

        return [.. Directory.GetFiles(PathUtil.GgufModelsPath, "*.gguf")
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool EmbeddingIndexIdentityChanged(ModelProviderConfig? current, ModelProviderConfig? requested)
    {
        if (current is null || requested is null)
        {
            return current is not null || requested is not null;
        }

        return current.Id != requested.Id ||
               current.VectorDimensions != requested.VectorDimensions ||
               !string.Equals(current.Type, requested.Type, StringComparison.Ordinal) ||
               !string.Equals(current.BaseUrl, requested.BaseUrl, StringComparison.Ordinal) ||
               !string.Equals(current.ModelId, requested.ModelId, StringComparison.Ordinal);
    }

}
