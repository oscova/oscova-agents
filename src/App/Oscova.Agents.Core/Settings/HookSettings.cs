using Microsoft.Extensions.Logging;
using Oscova.Agents.Settings;
using Oscova.Agents.Hooks;

namespace Oscova.Agents.Core.Settings;

/// <summary>
/// Stores system-wide outbound hook definitions for this Oscova host.
/// </summary>
public sealed class HookSettings : SettingsBase
{
    public const string Key = "hooks";

    private List<HookDefinition> _hooks = [];
    private List<HookApiKeyDefinition> _apiKeys = [];
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public HookSettings(IAppSettingsRepository repository, ILoggerFactory loggerFactory)
        : base(repository, WellKnownUsers.SystemId, loggerFactory) { }

    public override string SettingsKey => Key;

    public IReadOnlyList<HookDefinition> Hooks => _hooks;

    public IReadOnlyList<HookApiKeyDefinition> ApiKeys => _apiKeys;

    /// <summary>Runs one hook settings operation without overlapping another.</summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Runs one hook settings operation without overlapping another.</summary>
    public Task RunExclusiveAsync(Func<Task> action, CancellationToken ct = default)
    {
        return RunExclusiveAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }, ct);
    }

    /// <summary>
    /// Replaces every configured hook after normalization and invariant validation.
    /// </summary>
    public void ReplaceHooks(IEnumerable<HookDefinition> hooks)
    {
        _hooks = [.. HookSettingsValidator.NormalizeOrThrow(hooks)];
        OnPropertyChanged(nameof(Hooks));
        OnChanged();
    }

    /// <summary>
    /// Replaces all inbound hook API-key metadata after normalization and validation.
    /// </summary>
    public void ReplaceApiKeys(IEnumerable<HookApiKeyDefinition> apiKeys)
    {
        _apiKeys = [.. HookSettingsValidator.NormalizeApiKeysOrThrow(apiKeys)];
        OnPropertyChanged(nameof(ApiKeys));
        OnChanged();
    }

    /// <summary>
    /// Inserts or replaces one inbound hook API-key metadata record.
    /// </summary>
    public void UpsertApiKey(HookApiKeyDefinition apiKey)
    {
        var apiKeys = _apiKeys.Where(key => !string.Equals(key.Id, apiKey.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        apiKeys.Add(apiKey);
        ReplaceApiKeys(apiKeys);
    }

    /// <summary>
    /// Deletes an inbound hook API-key metadata record by id.
    /// </summary>
    public bool DeleteApiKey(string id)
    {
        var apiKeys = _apiKeys
            .Where(key => !string.Equals(key.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (apiKeys.Count == _apiKeys.Count)
        {
            return false;
        }

        ReplaceApiKeys(apiKeys);
        return true;
    }

    /// <summary>
    /// Creates a detached data snapshot for import/export and persistence.
    /// </summary>
    public Snapshot CreateSnapshot()
    {
        return new Snapshot { Hooks = [.. _hooks], ApiKeys = [.. _apiKeys] };
    }

    /// <summary>
    /// Applies values from a detached snapshot onto this settings instance.
    /// </summary>
    public void ApplyFrom(Snapshot data)
    {
        ReplaceHooks(data.Hooks ?? []);
        ReplaceApiKeys(data.ApiKeys ?? []);
    }

    public override string ToYaml()
    {
        return Yaml.Serialize(CreateSnapshot());
    }

    public override void FromYaml(string yaml)
    {
        var data = Yaml.Deserialize<Snapshot>(yaml);
        if (data is not null)
        {
            ApplyFrom(data);
        }
    }

    /// <summary>
    /// Plain data object for serialization and settings import/export.
    /// </summary>
    public sealed class Snapshot
    {
        public IList<HookDefinition> Hooks { get; set; } = [];
        public IList<HookApiKeyDefinition> ApiKeys { get; set; } = [];
    }
}

/// <summary>
/// Stores non-recoverable metadata for one inbound hook bearer token.
/// </summary>
public sealed class HookApiKeyDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string TokenPrefix { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public IList<string> Scopes { get; set; } = [];
    public IList<string> AllowedTaskSlugs { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>
/// Describes one outbound hook handler and the events it receives.
/// </summary>
public sealed class HookDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public IList<HookEventKind> Events { get; set; } = [];
    public HookHandlerType HandlerType { get; set; }
    public HookFailureMode FailureMode { get; set; } = HookFailureMode.FailClosed;
    public int TimeoutSeconds { get; set; } = 10;
    public CommandHookOptions? Command { get; set; }
    public HttpHookOptions? Http { get; set; }
}

/// <summary>
/// Configures execution of a command hook process.
/// </summary>
public sealed class CommandHookOptions
{
    public string FileName { get; set; } = string.Empty;
    public IList<string> Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
}

/// <summary>
/// Configures an outbound HTTP hook request.
/// </summary>
public sealed class HttpHookOptions
{
    public string Url { get; set; } = string.Empty;
    public IList<HookHeader> Headers { get; set; } = [];
    public IList<HookSecretHeader> SecretHeaders { get; set; } = [];
}

/// <summary>
/// Stores a non-secret static HTTP header for hook requests.
/// </summary>
public sealed class HookHeader
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Points an HTTP hook header at a system credential field.
/// </summary>
public sealed class HookSecretHeader
{
    public string Name { get; set; } = string.Empty;
    public string CredentialKey { get; set; } = string.Empty;
    public string? FieldName { get; set; }
}
