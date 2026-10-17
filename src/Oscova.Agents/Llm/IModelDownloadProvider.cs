using System;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Llm;

/// <summary>Downloads model assets into the configured provider's managed cache.</summary>
public interface IModelDownloadProvider
{
    /// <summary>Downloads the configured model and reports transfer and preparation progress.</summary>
    Task DownloadModelAsync(
        ModelProviderConfig config,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
