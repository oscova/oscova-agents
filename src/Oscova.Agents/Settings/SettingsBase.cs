using Microsoft.Extensions.Logging;
using Oscova.Agents.Persistence;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Settings;

/// <summary>Provides persistence and change notification for a settings domain.</summary>
public abstract class SettingsBase : INotifyPropertyChanged
{
    /// <summary>Serializes and deserializes settings content.</summary>
    protected static readonly YamlConverter Yaml = new();

    private readonly IAppSettingsRepository _repository;
    private readonly string _userId;
    private readonly ILogger _logger;
    private int _notificationSuppressionDepth;
    private string? _persistedContent;

    /// <summary>Initializes a settings domain for a repository and scope.</summary>
    protected SettingsBase(IAppSettingsRepository repository, string userId, ILoggerFactory loggerFactory)
    {
        _repository = repository;
        _userId = userId;
        _logger = loggerFactory.CreateLogger(GetType());
    }

    /// <summary>Fires when a property value changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Fires when any property value changes unless notifications are suppressed.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the key used to store and load this settings domain.</summary>
    public abstract string SettingsKey { get; }

    /// <summary>Identifies the persisted shape supported by this settings domain.</summary>
    protected virtual int SchemaVersion => 1;

    /// <summary>Converts supported older raw settings before version validation and current model binding.</summary>
    protected virtual string PreparePersistedYaml(string yaml) => yaml;

    /// <summary>Gets whether the initial load has completed.</summary>
    public bool IsLoaded { get; private set; }

    internal bool SuppressNotifications => Volatile.Read(ref _notificationSuppressionDepth) > 0;

    /// <summary>Loads this settings domain once.</summary>
    public async Task LoadAsync()
    {
        if (IsLoaded)
        {
            return;
        }

        await ReloadAsync().ConfigureAwait(false);
    }

    /// <summary>Reloads this settings domain from the repository.</summary>
    public async Task ReloadAsync()
    {
        Interlocked.Increment(ref _notificationSuppressionDepth);
        try
        {
            if (!string.IsNullOrWhiteSpace(_userId))
            {
                var content = await _repository.GetSettingsAsync(_userId, SettingsKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    var prepared = PreparePersistedYaml(content);
                    Yaml.RequireSchemaVersion(prepared, SchemaVersion);
                    FromYaml(prepared);
                    if (!string.Equals(content, prepared, StringComparison.Ordinal))
                    {
                        await _repository.SaveSettingsAsync(_userId, SettingsKey, prepared).ConfigureAwait(false);
                    }
                }
            }

            _persistedContent = CreatePersistedYaml();
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {SettingsKey} settings.", SettingsKey);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _notificationSuppressionDepth);
        }
    }

    /// <summary>Persists this settings domain and restores its last durable state when the write fails.</summary>
    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_userId))
        {
            _logger.LogWarning("Attempted to save {SettingsKey} settings but no authenticated user was found.", SettingsKey);
            return;
        }

        var content = CreatePersistedYaml();
        try
        {
            await _repository.SaveSettingsAsync(_userId, SettingsKey, content).ConfigureAwait(false);
            _persistedContent = content;
        }
        catch
        {
            RestorePersistedContent();
            throw;
        }
    }

    /// <summary>Attempts persistence without rolling back in-memory changes when the write fails.</summary>
    public async Task<bool> TrySaveAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_userId))
            {
                _logger.LogWarning("Attempted to save {SettingsKey} settings but no authenticated user was found.", SettingsKey);
                return false;
            }

            var content = CreatePersistedYaml();
            await _repository.SaveSettingsAsync(_userId, SettingsKey, content).ConfigureAwait(false);
            _persistedContent = content;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save {SettingsKey} settings.", SettingsKey);
            return false;
        }
    }

    /// <summary>Serializes this settings instance to YAML.</summary>
    public abstract string ToYaml();

    /// <summary>Applies values from a YAML string to this instance.</summary>
    public abstract void FromYaml(string yaml);

    private string CreatePersistedYaml() => YamlConverter.WithSchemaVersion(ToYaml(), SchemaVersion);

    /// <summary>Updates a property and raises its change notifications when the value differs.</summary>
    protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        OnChanged();
    }

    /// <summary>Raises the property change notification unless notifications are suppressed.</summary>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (SuppressNotifications)
        {
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>Raises the domain change notification unless notifications are suppressed.</summary>
    protected void OnChanged()
    {
        if (SuppressNotifications)
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RestorePersistedContent()
    {
        if (_persistedContent is null)
        {
            return;
        }

        Interlocked.Increment(ref _notificationSuppressionDepth);
        try
        {
            FromYaml(_persistedContent);
        }
        finally
        {
            Interlocked.Decrement(ref _notificationSuppressionDepth);
        }

        OnPropertyChanged(string.Empty);
        OnChanged();
    }
}
