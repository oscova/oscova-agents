namespace Oscova.Agents.Core.Interfaces;

public enum DownloadStatus
{
    Pending,
    Downloading,
    Completed,
    Failed
}

public sealed class DownloadProgressInfo
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public required string DestinationPath { get; init; }
    public string FileName => Path.GetFileName(DestinationPath);
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public double? Percent => TotalBytes > 0 ? (double)BytesReceived / TotalBytes.Value * 100d : null;
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public Exception? Error { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public interface IDownloadService
{
    Task EnsureDownloadAsync(string id, string url, string destinationPath, CancellationToken ct = default);

    IReadOnlyCollection<DownloadProgressInfo> ActiveDownloads { get; }

    event EventHandler? DownloadsChanged;

    bool TryGet(string destinationPath, out DownloadProgressInfo? info);

    bool TryCancel(string destinationPath);
}
