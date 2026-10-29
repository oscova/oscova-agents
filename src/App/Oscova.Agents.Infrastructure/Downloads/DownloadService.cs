using Microsoft.Extensions.Logging;
using Oscova.Agents.Core.Interfaces;
using System.Collections.Concurrent;

namespace Oscova.Agents.Infrastructure.Downloads;

public sealed class DownloadService(IHttpClientFactory httpClientFactory, ILogger<DownloadService> logger) : IDownloadService
{
    private readonly IHttpClientFactory _httpFactory = httpClientFactory;
    private readonly ILogger<DownloadService> _logger = logger;

    private readonly ConcurrentDictionary<string, DownloadProgressInfo> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProgressNotificationThrottle _notifications = new();

    public event EventHandler? DownloadsChanged;

    public IReadOnlyCollection<DownloadProgressInfo> ActiveDownloads => _active.Values.ToArray();

    public bool TryGet(string destinationPath, out DownloadProgressInfo? info)
    {
        return _active.TryGetValue(destinationPath, out info);
    }

    public bool TryCancel(string destinationPath)
    {
        if (!_cts.TryGetValue(destinationPath, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already completed and disposed; nothing to cancel.
        }

        return true;
    }

    public Task EnsureDownloadAsync(string id, string url, string destinationPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (File.Exists(destinationPath))
        {
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        // Reserve cancellation and writer ownership before any worker can start.
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_cts.TryAdd(destinationPath, linkedCts))
        {
            linkedCts.Dispose();
            return Task.CompletedTask;
        }

        var info = _active.GetOrAdd(destinationPath, _ => new DownloadProgressInfo
        {
            Id = id,
            Url = url,
            DestinationPath = destinationPath,
            Status = DownloadStatus.Pending,
        });

        var downloadToken = linkedCts.Token;

        // Status transitions must reach subscribers immediately; per-chunk progress is rate-limited
        // so a fast download doesn't fire thousands of events.
        void NotifyStatusChange()
        {
            _notifications.TryNotify(force: true);
            try { DownloadsChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        void NotifyProgress()
        {
            if (!_notifications.TryNotify()) { return; }

            try { DownloadsChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var http = _httpFactory.CreateClient("downloads");
                info.Status = DownloadStatus.Downloading;
                info.StartedAt = DateTimeOffset.UtcNow;
                NotifyStatusChange();

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, downloadToken).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                info.TotalBytes = resp.Content.Headers.ContentLength;

                var httpStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (httpStream.ConfigureAwait(false))
                {
                    await using var fs = new FileStream(destinationPath + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                int read;
                while ((read = await httpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), downloadToken).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), downloadToken).ConfigureAwait(false);
                    info.BytesReceived += read;
                    NotifyProgress();
                }

                await fs.FlushAsync(ct).ConfigureAwait(false);
                fs.Close();

                File.Move(destinationPath + ".part", destinationPath, overwrite: true);

                info.Status = DownloadStatus.Completed;
                info.CompletedAt = DateTimeOffset.UtcNow;
                NotifyStatusChange();
                }
            }
            catch (OperationCanceledException)
            {
                _active.TryRemove(destinationPath, out _);
                try
                {
                    if (File.Exists(destinationPath + ".part"))
                    {
                        File.Delete(destinationPath + ".part");
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to delete partial download file {Path}", destinationPath + ".part");
                }

                NotifyStatusChange();
            }
            catch (Exception ex)
            {
                info.Status = DownloadStatus.Failed;
                info.Error = ex;
                _logger.LogError(ex, "Download failed for {Path} from {Url}", destinationPath, url);
                try
                {
                    if (File.Exists(destinationPath + ".part"))
                    {
                        File.Delete(destinationPath + ".part");
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to delete partial download file {Path}", destinationPath + ".part");
                }

                NotifyStatusChange();
            }
            finally
            {
                if (_cts.TryRemove(destinationPath, out var cts))
                {
                    cts.Dispose();
                }
            }
        }); // Even an already-canceled admission must enter its cleanup.

        return Task.CompletedTask;
    }
}
