using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Oscova.Agents.Core;
using Oscova.Agents.Core.ApiContracts.Audit;
using Oscova.Agents.Core.ApiContracts.Files;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Library;
using Oscova.Agents.Core.Notifications;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Infrastructure.Memory;
using Oscova.Agents.Notifications;
using Oscova.Agents.Policies;
using Oscova.Agents.Utils;
using Oscova.Agents.Vector;
using System.Threading.Channels;

namespace Oscova.Agents.Infrastructure.Library;

/// <summary>Reconciles one user's Library files with their rebuildable vector index.</summary>
public sealed partial class LibraryIndexingService : ILibraryIndexingService
{
    internal const string CollectionName = "library_files";
    private const string VectorDatabaseFileName = "vectors.db";
    private const string OversizedReason = "The file exceeds the 10 MB Library indexing limit.";
    private const string UnsupportedReason = "This file type is not supported by Library Search.";
    private const string EmptyTextReason = "The file does not contain indexable text.";
    private const string EncryptedPdfReason = "The PDF is encrypted and cannot be indexed.";
    private const string ImageOnlyPdfReason = "The PDF does not contain extractable text.";

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".pdf" };

    private readonly string _userId;
    private readonly IEmbeddingGeneratorFactory _embeddingGeneratorFactory;
    private readonly ModelProviderSettings _modelProviderSettings;
    private readonly PrivacySettings _privacySettings;
    private readonly IUserDirectoryService _userDirectoryService;
    private readonly IEgressAuditStore _egressAuditStore;
    private readonly NotificationHub<NotificationMessage> _notificationMessages;
    private readonly ILogger<LibraryIndexingService> _logger;
    private readonly DurableVectorIndex _index;
    private readonly string _vectorDatabasePath;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly Channel<byte> _requests = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Lock _requestLock = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private readonly HashSet<string> _invalidatedPaths = new(PathComparer);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Task _workerTask;
    private bool _invalidateAll;
    private int _disposed;

    public LibraryIndexingService(
        string userId,
        IEmbeddingGeneratorFactory embeddingGeneratorFactory,
        ModelProviderSettings modelProviderSettings,
        PrivacySettings privacySettings,
        IUserDirectoryService userDirectoryService,
        IEgressAuditStore egressAuditStore,
        NotificationHub<NotificationMessage> notificationMessages,
        ILogger<LibraryIndexingService> logger,
        Oscova.Agents.Core.Secrets.IVaultDataProtector vault)
    {
        _userId = userId;
        _embeddingGeneratorFactory = embeddingGeneratorFactory;
        _modelProviderSettings = modelProviderSettings;
        _privacySettings = privacySettings;
        _userDirectoryService = userDirectoryService;
        _egressAuditStore = egressAuditStore;
        _notificationMessages = notificationMessages;
        _logger = logger;
        _lifetimeCts = new CancellationTokenSource();

        string memoryDirectory = userDirectoryService.GetMemoryDirectoryPath(userId);
        _vectorDatabasePath = Path.Combine(memoryDirectory, VectorDatabaseFileName);
        _index = EncryptedVectorStorage.CreateIndex(
            vault,
            userId,
            _vectorDatabasePath,
            Path.Combine(memoryDirectory, UserFileNames.LibraryIndexManifestFile),
            CollectionName,
            logger);
        _workerTask = ProcessQueueAsync(_lifetimeCts.Token);
    }

    /// <inheritdoc />
    public ValueTask QueueReconciliationAsync(
        CancellationToken cancellationToken = default,
        string? changedRelativePath = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_requestLock)
        {
            if (string.IsNullOrWhiteSpace(changedRelativePath))
            {
                _invalidateAll = true;
            }
            else
            {
                _invalidatedPaths.Add(
                    PathHelper.ToSlashSeparatedRelativePath(changedRelativePath).Trim('/'));
            }

            // A capacity-one channel is the coalescing "reconcile again" bit.
            _requests.Writer.TryWrite(0);
        }

        return ValueTask.CompletedTask;
    }

    internal async Task<bool> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        BatchOutcome outcome = await RunReconciliationAsync(cancellationToken).ConfigureAwait(false);
        await PublishBatchOutcomeAsync(outcome).ConfigureAwait(false);
        return outcome.Completed;
    }

    /// <inheritdoc />
    public async Task<LibraryIndexView> GetSnapshotAsync(
        bool requireLocalEndpoint = false,
        CancellationToken cancellationToken = default)
    {
        DurableVectorIndexSnapshot load =
            await _index.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        GeneratorResolution resolution = await TryCreateGeneratorAsync(
            _privacySettings.IsLocalOnly || requireLocalEndpoint,
            cancellationToken,
            logFailure: false).ConfigureAwait(false);
        bool providerAvailable = resolution.Generator is not null;
        resolution.Generator?.Dispose();
        EmbeddingEgressInfo? egress = _embeddingGeneratorFactory.DescribeEgress();

        return new LibraryIndexView
        {
            EmbeddingProviderName = _modelProviderSettings.SelectedEmbeddingModelProvider?.Name,
            EmbeddingModelId = _modelProviderSettings.SelectedEmbeddingModelProvider?.ModelId,
            EgressClass = egress.HasValue
                ? egress.Value.IsCloud ? EgressClass.Cloud : EgressClass.Local
                : null,
            IsBlockedByLocalOnlyPolicy = resolution.IsBlockedByLocalOnlyPolicy,
            IsEmbeddingProviderAvailable = providerAvailable,
            UnavailableReason = resolution.UnavailableReason,
            Files = BuildSnapshotFiles(load, providerAvailable)
        };
    }

    private DurableVectorSpace? GetCurrentFingerprint() =>
        EmbeddingGeneratorFactory.CreateVectorSpace(
            _modelProviderSettings.SelectedEmbeddingModelProvider);

    private bool IsSourceInvalidated(string relativePath)
    {
        lock (_requestLock)
        {
            return _invalidateAll || _invalidatedPaths.Any(path => IsSameOrChildPath(relativePath, path));
        }
    }

    internal async Task<LibraryIndexStatus> GetIndexStatusAsync(
        bool requireLocalEndpoint,
        CancellationToken cancellationToken)
    {
        // Status is read-only: it checks provider eligibility and manifest state without queueing a rebuild.
        DurableVectorIndexSnapshot load =
            await _index.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        GeneratorResolution resolution = await TryCreateGeneratorAsync(
            _privacySettings.IsLocalOnly || requireLocalEndpoint,
            cancellationToken,
            logFailure: false).ConfigureAwait(false);
        bool providerAvailable = resolution.Generator is not null;
        resolution.Generator?.Dispose();
        var status = BuildStatus(load, providerAvailable, resolution.IsBlockedByLocalOnlyPolicy);
        return resolution.UnavailableReason is null ? status : status with { Message = resolution.UnavailableReason };
    }

    internal async Task WriteEmbeddingEgressAsync(
        string source,
        CancellationToken cancellationToken)
    {
        EmbeddingEgressInfo? info = _embeddingGeneratorFactory.DescribeEgress();
        if (info is not { IsCloud: true } egress)
        {
            return;
        }

        try
        {
            await _egressAuditStore.AppendAsync(_userId, new EgressAuditRecord
            {
                Kind = EgressAuditKinds.Embedding,
                Source = source,
                ProviderId = egress.ProviderId,
                ModelId = egress.ModelId
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            using IDisposable? scope = BeginNotificationHandledScope();
            _logger.LogWarning(
                ex,
                "Failed to append Library embedding egress audit for source {EgressSource}.",
                source);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _requests.Writer.TryComplete();
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }

        _mutationGate.Dispose();
        _index.Dispose();
        _lifetimeCts.Dispose();
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _requests.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _requests.Reader.TryRead(out _);
                bool runAgain;
                do
                {
                    runAgain = false;
                    bool completed = await TryReconcileAsync(cancellationToken).ConfigureAwait(false);

                    lock (_requestLock)
                    {
                        runAgain = _requests.Reader.TryRead(out _);
                        if (completed && !runAgain)
                        {
                            _invalidateAll = false;
                            _invalidatedPaths.Clear();
                        }
                    }
                }
                while (runAgain);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Runs one reconciliation without allowing a recoverable failure to stop the per-user worker.</summary>
    private async Task<bool> TryReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A provider pause leaves edited sources invalid until a later scan completes.
            return await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            using IDisposable? scope = BeginNotificationHandledScope();
            _logger.LogError(ex, "Library reconciliation failed for user {UserId}.", _userId);
            return false;
        }
    }

    private async Task<BatchOutcome> RunReconciliationAsync(CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<GeneratorResolution> TryCreateGeneratorAsync(
        bool requireLocalEndpoint,
        CancellationToken cancellationToken,
        bool logFailure = true)
    {
        DurableVectorSpace? space = GetCurrentFingerprint();
        if (space is null)
        {
            return default;
        }

        try
        {
            // Creating the client validates configuration and policy; egress starts only at GenerateAsync.
            var resolution =
                await _embeddingGeneratorFactory.CreateAsync(
                    requireLocalEndpoint,
                    cancellationToken).ConfigureAwait(false);
            return new GeneratorResolution(resolution.Generator, space, false, resolution.UnavailableReason);
        }
        catch (LocalOnlyEmbeddingProviderBlockedException)
        {
            return new GeneratorResolution(null, space, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or NotSupportedException or
                FormatException or HttpRequestException)
        {
            if (logFailure)
            {
                using IDisposable? scope = BeginNotificationHandledScope();
                _logger.LogWarning(ex, "The Library embedding provider is unavailable for user {UserId}.", _userId);
            }

            return new GeneratorResolution(null, space, false);
        }
    }

    private IReadOnlyList<LibraryFileIndexItemView> BuildSnapshotFiles(
        DurableVectorIndexSnapshot load,
        bool providerAvailable)
    {
        var files = load.Sources.ToDictionary(
            static pair => pair.Value.Metadata ?? pair.Key,
            static pair => new LibraryFileIndexItemView
            {
                Path = pair.Value.Metadata ?? pair.Key,
                State = ToLibraryState(pair.Value.State),
                Reason = pair.Value.Reason
            },
            PathComparer);
        if (providerAvailable)
        {
            lock (_requestLock)
            {
                if (_invalidateAll)
                {
                    // Snapshot the keys because replacing dictionary values invalidates its enumerator.
                    foreach (string path in files.Keys.ToList())
                    {
                        files[path] = CreateQueuedItem(path);
                    }
                }

                foreach (string invalidatedPath in _invalidatedPaths)
                {
                    foreach (string path in files.Keys
                                 .Where(path => IsSameOrChildPath(path, invalidatedPath))
                                 .ToList())
                    {
                        files[path] = CreateQueuedItem(path);
                    }

                    if (TryResolveSourcePath(invalidatedPath, out string sourcePath) &&
                        File.Exists(sourcePath))
                    {
                        files[invalidatedPath] = CreateQueuedItem(invalidatedPath);
                    }
                }
            }
        }

        return [.. files.Values.OrderBy(
            static entry => entry.Path,
            PathComparer)];
    }

    private static LibraryFileIndexItemView CreateQueuedItem(string path) =>
        new() { Path = path, State = LibraryFileIndexState.Queued };

    private static string GetSourceId(string relativePath) => OperatingSystem.IsWindows()
        ? relativePath.ToUpperInvariant()
        : relativePath;

    private static LibraryFileIndexState ToLibraryState(DurableVectorSourceState state) => state switch
    {
        DurableVectorSourceState.Queued => LibraryFileIndexState.Queued,
        DurableVectorSourceState.Pending => LibraryFileIndexState.Indexing,
        DurableVectorSourceState.Committed => LibraryFileIndexState.Ready,
        DurableVectorSourceState.Skipped => LibraryFileIndexState.Skipped,
        DurableVectorSourceState.Failed => LibraryFileIndexState.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static bool IsSameOrChildPath(string candidate, string path) =>
        PathComparer.Equals(candidate, path) ||
        candidate.StartsWith(path + "/", PathComparison);

    private LibraryIndexStatus BuildStatus(
        DurableVectorIndexSnapshot load,
        bool providerAvailable,
        bool isBlockedByLocalOnlyPolicy)
    {
        IReadOnlyList<string> queued = GetQueuedPaths(load);
        IReadOnlyList<string> indexing = GetPaths(load, LibraryFileIndexState.Indexing);
        IReadOnlyList<string> failed = GetPaths(load, LibraryFileIndexState.Failed);

        LibraryIndexOverallState state;
        string? message = null;
        if (!providerAvailable)
        {
            state = LibraryIndexOverallState.Unavailable;
            message = isBlockedByLocalOnlyPolicy
                ? "Library Search is blocked because the selected embedding provider is not local."
                : "Library Search is unavailable until a usable embedding provider is configured.";
        }
        else if (!IsFingerprintCurrent(load, GetCurrentFingerprint()))
        {
            // A mismatched provider cannot safely search vectors created in another embedding space.
            state = LibraryIndexOverallState.Rebuilding;
            message = "Library indexing is rebuilding for the selected embedding provider.";
        }
        else
        {
            bool incomplete = queued.Count > 0 || indexing.Count > 0 || failed.Count > 0;
            state = incomplete ? LibraryIndexOverallState.Incomplete : LibraryIndexOverallState.Ready;
        }

        return new LibraryIndexStatus
        {
            State = state,
            Message = message,
            QueuedPaths = queued,
            IndexingPaths = indexing,
            FailedPaths = failed
        };
    }

    private static IReadOnlyList<string> GetPaths(
        DurableVectorIndexSnapshot load,
        LibraryFileIndexState state) =>
        [.. load.Sources
            .Where(pair => ToLibraryState(pair.Value.State) == state)
            .Select(static pair => pair.Value.Metadata ?? pair.Key)
            .Order(PathComparer)];

    private IReadOnlyList<string> GetQueuedPaths(DurableVectorIndexSnapshot load)
    {
        var paths = GetPaths(load, LibraryFileIndexState.Queued)
            .ToHashSet(PathComparer);
        lock (_requestLock)
        {
            if (_invalidateAll)
            {
                paths.UnionWith(load.Sources.Select(
                    static pair => pair.Value.Metadata ?? pair.Key));
            }
            else
            {
                paths.UnionWith(_invalidatedPaths);
            }
        }

        return [.. paths.Order(PathComparer)];
    }

    private static bool IsFingerprintCurrent(
        DurableVectorIndexSnapshot load,
        DurableVectorSpace? current) =>
        !load.Exists && load.Sources.Count == 0 ||
        current is not null &&
        load.IsTrusted &&
        string.Equals(load.Fingerprint, current.Fingerprint, StringComparison.Ordinal) &&
        load.VectorDimensions == current.VectorDimensions;

    private int GetConfiguredDimensions() =>
        Math.Max(1, _modelProviderSettings.SelectedEmbeddingModelProvider?.VectorDimensions ?? 768);

    private IDisposable? BeginNotificationHandledScope() =>
        _logger.BeginScope(new Dictionary<string, object?>
        {
            ["userId"] = _userId,
            ["notificationHandled"] = true
        });

    private async Task PublishBatchOutcomeAsync(BatchOutcome outcome)
    {
        if (outcome.ProcessedCount == 0)
        {
            return;
        }

        NotificationMessage message = outcome.FailedCount == 0
            ? new NotificationMessage(
                "Library indexing complete",
                $"Library Search updated {outcome.ProcessedCount} file(s).",
                NotificationKind.Generic,
                UserId: _userId)
            : new NotificationMessage(
                "Library indexing needs attention",
                $"{outcome.FailedCount} Library file(s) failed to index. Open Settings > Files > Library for details.",
                NotificationKind.SystemWarning,
                UserId: _userId);

        try
        {
            await _notificationMessages.NotifyAsync(_userId, message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            using IDisposable? scope = BeginNotificationHandledScope();
            _logger.LogWarning(ex, "Failed to publish the Library indexing outcome for user {UserId}.", _userId);
        }
    }

    private readonly record struct GeneratorResolution(
        IEmbeddingGenerator<string, Embedding<float>>? Generator,
        DurableVectorSpace? Space,
        bool IsBlockedByLocalOnlyPolicy,
        string? UnavailableReason = null);
}
