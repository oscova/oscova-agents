using System;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Llm;

/// <summary>Downloads provider-owned model assets into the provider's local cache.</summary>
public interface IModelDownloadProvider
{
    /// <summary>Downloads the selected model and reports percentage progress.</summary>
    Task DownloadModelAsync(
        string modelId,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default);
}
