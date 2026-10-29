using System.Diagnostics.CodeAnalysis;

namespace Oscova.Agents.Core.Conversation;

/// <summary>
/// Provides ambient access to the current app-owned <see cref="RunContext"/> via AsyncLocal.
/// </summary>
[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "<Pending>")]
public sealed class RunContextAccessor
{
    private static readonly AsyncLocal<RunContext?> _current = new();
    private static readonly AsyncLocal<RunContextScope?> _currentScope = new();

    public RunContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    public RunContextScope? CurrentScope => _currentScope.Value;

    /// <summary>Attributes standalone provider dispatch to its consumer while preserving an existing run's origin.</summary>
    public RunContextScope SetUserRequestContext(string userId, string executionId) => SetContext(
        Current is { } current && current.UserId == userId ? current : new RunContext
        {
            UserId = userId,
            ExecutionId = executionId,
            ExecutionKind = RunExecutionKind.WebChat
        });

    /// <summary>
    /// Sets the ambient run context and returns the scope that owns run-scoped resources.
    /// </summary>
    public RunContextScope SetContext(RunContext context)
    {
        var previous = _current.Value;
        var previousScope = _currentScope.Value;
        _current.Value = context;
        var scope = new RunContextScope(this, previous, previousScope);
        _currentScope.Value = scope;
        return scope;
    }

    private void Restore(RunContext? previousContext, RunContextScope? previousScope)
    {
        _current.Value = previousContext;
        _currentScope.Value = previousScope;
    }

    /// <summary>
    /// Owns resources that should live only for the current agent run.
    /// </summary>
    public sealed class RunContextScope : IAsyncDisposable
    {
        private readonly RunContextAccessor _accessor;
        private readonly RunContext? _previousContext;
        private readonly RunContextScope? _previousScope;
        private readonly List<IAsyncDisposable> _resources = [];
        private bool _disposed;

        internal RunContextScope(
            RunContextAccessor accessor,
            RunContext? previousContext,
            RunContextScope? previousScope)
        {
            _accessor = accessor;
            _previousContext = previousContext;
            _previousScope = previousScope;
        }

        /// <summary>
        /// Registers a resource to dispose before the ambient run context is restored.
        /// </summary>
        public void Register(IAsyncDisposable resource)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _resources.Add(resource);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (int resourceIndex = _resources.Count - 1; resourceIndex >= 0; resourceIndex--)
            {
                try
                {
                    await _resources[resourceIndex].DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Teardown is best-effort; resource owners log failures they can diagnose.
                }
            }

            _accessor.Restore(_previousContext, _previousScope);
        }
    }
}

