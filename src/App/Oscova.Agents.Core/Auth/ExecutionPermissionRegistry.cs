using System.Collections.Concurrent;

namespace Oscova.Agents.Core.Auth;

/// <summary>Coordinates permission revocation and shared configuration changes with dependent execution.</summary>

public sealed class ExecutionPermissionRegistry
{
    private readonly ConcurrentDictionary<Registration, byte> _active = new();

    /// <summary>Notifies connected execution devices after affected local work receives cancellation.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0046", Justification = "Revocation awaits asynchronous subscriber cleanup.")]
    public event Func<string, IReadOnlyList<string>, Task>? Changed;

    private readonly Lock _configurationGate = new();
    private readonly SemaphoreSlim _configurationChanges = new(1, 1);
    private readonly HashSet<string> _changingConfigurations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _configurationVersions = new(StringComparer.Ordinal);

    /// <summary>Notifies affected accounts without exposing resource contents or credentials.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0046", Justification = "Execution interruption awaits notification delivery.")]
    public event Func<IReadOnlyList<string>, Task>? ConfigurationInterrupted;

    /// <summary>Releases retained runtime resources while admission to their configuration is closed.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0046", Justification = "Configuration changes await retained connection disposal.")]
    public event Func<string, Task>? ConfigurationChanged;

    /// <summary>Captures the configuration version before constructing a client or reading its credentials.</summary>
    public long GetConfigurationVersion(string key)
    {
        lock (_configurationGate)
        {
            if (_changingConfigurations.Contains(key))
            {
                throw new InvalidOperationException("This configuration is changing. Start a new run after the change finishes.");
            }

            return _configurationVersions.GetValueOrDefault(key);
        }
    }

    /// <summary>Registers current work atomically with configuration-change admission checks.</summary>
    public Registration RegisterConfiguration(string key, long version, string userId,
        CancellationToken ct, Func<Task>? stop = null)
    {
        lock (_configurationGate)
        {
            if (_changingConfigurations.Contains(key) || _configurationVersions.GetValueOrDefault(key) != version)
            {
                throw new InvalidOperationException("This configuration changed. Start a new run to use its current settings.");
            }

            var registration = new Registration(this, userId, [], ct, stop) { ConfigurationKey = key };
            _active.TryAdd(registration, 0);
            return registration;
        }
    }

    /// <summary>Stops dependent work and releases its runtime resources before applying a configuration change.</summary>
    public async Task ChangeConfigurationAsync(Func<IReadOnlyList<string>> getKeys, Func<Task> apply, bool confirmed, CancellationToken ct)
    {
        // Serialize edits without blocking admission for unrelated resources.
        await _configurationChanges.WaitAsync(ct).ConfigureAwait(false);
        var changing = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            changing.UnionWith(getKeys());
            Registration[] affected;
            lock (_configurationGate)
            {
                affected = _active.Keys.Where(registration => registration.ConfigurationKey is { } key && changing.Contains(key)).ToArray();
                if (affected.Length > 0 && !confirmed) { throw new ConfirmationRequiredException(); }

                _changingConfigurations.UnionWith(changing);
            }

            var users = affected.Select(registration => registration.UserId).Distinct(StringComparer.Ordinal).ToArray();
            var notifications = ConfigurationInterrupted?.GetInvocationList().Cast<Func<IReadOnlyList<string>, Task>>().ToArray() ?? [];
            // Start cancellation even if a notification cannot be delivered.
            await Task.WhenAll(affected.Select(registration => registration.CancelAsync())
                .Concat(users.Length == 0 ? [] : notifications.Select(notify => NotifyInterruptedAsync(notify, users))))
                .WaitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(affected.Select(registration => registration.Completion.Task)).WaitAsync(ct).ConfigureAwait(false);

            // Invalidate retained clients only after cleanup allows replacement to begin.
            lock (_configurationGate)
            {
                foreach (var key in changing)
                {
                    _configurationVersions[key] = _configurationVersions.GetValueOrDefault(key) + 1;
                }
            }

            var subscribers = ConfigurationChanged?.GetInvocationList().Cast<Func<string, Task>>().ToArray() ?? [];
            foreach (var key in changing)
            {
                await Task.WhenAll(subscribers.Select(subscriber => NotifyConfigurationAsync(subscriber, key))).WaitAsync(ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            await apply().ConfigureAwait(false);
        }
        finally
        {
            lock (_configurationGate) { _changingConfigurations.ExceptWith(changing); }

            _configurationChanges.Release();
        }
    }

    /// <summary>Requests confirmation before a settings edit interrupts admitted work.</summary>
    public sealed class ConfirmationRequiredException() : InvalidOperationException(
        "Confirm that this change may interrupt work using the affected configuration.") { }

    private static async Task NotifyInterruptedAsync(Func<IReadOnlyList<string>, Task> subscriber, IReadOnlyList<string> users) =>
        await subscriber(users).ConfigureAwait(false);

    private static async Task NotifyConfigurationAsync(Func<string, Task> subscriber, string key) =>
        await subscriber(key).ConfigureAwait(false);

    /// <summary>Keeps configuration dependencies attached to the complete agent and tool loop.</summary>
    public sealed class ConfigurationScope(string userId, CancellationToken ct, Func<Task>? stop = null) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        private readonly Dictionary<string, ExecutionPermissionRegistry.Registration> _registrations = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();
        private bool _disposed;

        public CancellationToken Token => _cancellation.Token;

        /// <summary>Retains a dependency after its model or tool call finishes, until the run ends.</summary>
        public void Track(ExecutionPermissionRegistry registry, string key, long version)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_registrations.ContainsKey(key))
                {
                    _registrations.Add(key, registry.RegisterConfiguration(key, version, userId, Token, CancelAsync));
                }
            }
        }

        private async Task CancelAsync()
        {
            Task cancellation;
            lock (_gate)
            {
                if (_disposed) { return; }

                cancellation = _cancellation.CancelAsync();
            }

            try { await cancellation.ConfigureAwait(false); }
            finally { if (stop is not null) { await stop().ConfigureAwait(false); } }
        }

        /// <summary>Releases registrations after the complete tool loop has stopped using them.</summary>
        public void Dispose()
        {
            ExecutionPermissionRegistry.Registration[] registrations;
            lock (_gate)
            {
                if (_disposed) { return; }

                _disposed = true;
                registrations = _registrations.Values.ToArray();
            }
            // Registration cancellation can call back into this scope, so release our lock first.
            foreach (var registration in registrations) { registration.Dispose(); }

            _cancellation.Dispose();
        }
    }
    /// <summary>Registers work before authorization so a concurrent revocation cannot miss its admission.</summary>
    public Registration Register(string userId, IReadOnlyList<string> requiredPermissions,
        CancellationToken ct, Func<Task>? stop = null)
    {
        var registration = new Registration(this, userId, requiredPermissions.ToArray(), ct, stop);
        _active.TryAdd(registration, 0);
        return registration;
    }

    /// <summary>Requests cancellation and cleanup for affected work, then updates connected devices.</summary>
    public async Task ApplyAsync(string userId, IReadOnlyList<string> granted)
    {
        var affected = _active.Keys.Where(registration => registration.UserId == userId &&
            registration.RequiredPermissions.Any(permission => !granted.Contains(permission, StringComparer.Ordinal))).ToArray();
        var changes = Changed?.GetInvocationList().Cast<Func<string, IReadOnlyList<string>, Task>>().ToArray() ?? [];
        // Start every cancellation and notification even when one cleanup fails.
        await Task.WhenAll(affected.Select(registration => registration.CancelAsync())
            .Concat(changes.Select(change => NotifyAsync(change, userId, granted)))).ConfigureAwait(false);
    }

    private static async Task NotifyAsync(Func<string, IReadOnlyList<string>, Task> change,
        string userId, IReadOnlyList<string> granted) =>
        await change(userId, granted).ConfigureAwait(false);

    /// <summary>Owns the cancellation token and optional process cleanup for one admitted operation.</summary>
    public sealed class Registration : IDisposable
    {
        private readonly ExecutionPermissionRegistry _owner;
        private readonly CancellationTokenSource _cancellation;
        private readonly Func<Task>? _stop;
        private readonly Lock _gate = new();
        private bool _disposed;
        private Task? _cancelTask;

        internal Registration(ExecutionPermissionRegistry owner, string userId,
            IReadOnlyList<string> requiredPermissions, CancellationToken ct, Func<Task>? stop)
        {
            _owner = owner;
            UserId = userId;
            RequiredPermissions = requiredPermissions;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Token = _cancellation.Token;
            _stop = stop;
        }

        internal string? ConfigurationKey { get; init; }
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string UserId { get; }
        public IReadOnlyList<string> RequiredPermissions { get; }
        public CancellationToken Token { get; }

        /// <summary>Requests cancellation and waits for the operation's registered process cleanup.</summary>
        public Task CancelAsync()
        {
            lock (_gate)
            {
                if (_disposed) { return Task.CompletedTask; }

                return _cancelTask ??= CancelCoreAsync(_cancellation.CancelAsync());
            }
        }

        private async Task CancelCoreAsync(Task cancellation)
        {
            try { await cancellation.ConfigureAwait(false); }
            finally
            {
                if (_stop is not null) { await _stop().ConfigureAwait(false); }
            }
        }

        /// <summary>Removes completed work from permission revocation tracking.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) { return; }

                _disposed = true;
                _owner._active.TryRemove(this, out _);
                _cancellation.Dispose();
                Completion.TrySetResult();
            }
        }
    }
}
