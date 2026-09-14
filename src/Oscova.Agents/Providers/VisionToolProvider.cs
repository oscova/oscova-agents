using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Tools.Vision;
using Oscova.Agents.Workspace;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Providers;

/// <summary>
/// Registers <see cref="AnalyzeImageTool"/> as an agent tool. Intended to be added to the
/// provider chain only when the selected model provider supports images; registration itself
/// is the capability gate.
/// </summary>
public sealed class VisionToolProvider(
    IChatClient chatClient,
    Func<IReadOnlyList<AllowedDirectory>> workspaceResolver) : AIContextProvider
{
    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var tool = new AnalyzeImageTool(chatClient, workspaceResolver).AsAITool();
        return new ValueTask<AIContext>(new AIContext { Tools = [tool] });
    }
}
