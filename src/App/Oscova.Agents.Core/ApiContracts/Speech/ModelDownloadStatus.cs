using Oscova.Agents.Core.Interfaces;

namespace Oscova.Agents.Core.ApiContracts.Speech;

// Wire shape for speech model download progress polled from the Settings UI.
public sealed class ModelDownloadStatus
{
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public double? Percent { get; set; }
    public string? Error { get; set; }
}
