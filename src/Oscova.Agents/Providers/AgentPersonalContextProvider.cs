using Microsoft.Agents.AI;
using Oscova.Agents.Templating;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Providers;

/// <summary>
/// Renders host-provided personal context into agent instructions for a run.
/// </summary>
/// <param name="instructionTemplate">The Scriban template used to render personal instructions.</param>
/// <param name="modelFactory">The factory that supplies the template model for the current user or run.</param>
public sealed class AgentPersonalContextProvider(
    string instructionTemplate,
    Func<CancellationToken, Task<object>> modelFactory) : AIContextProvider
{
    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instructionTemplate))
        {
            return await base.ProvideAIContextAsync(context, cancellationToken).ConfigureAwait(false);
        }

        var model = await modelFactory(cancellationToken).ConfigureAwait(false);
        var instructions = PromptTemplateRenderer.Render(instructionTemplate, model);
        return new AIContext { Instructions = instructions };
    }
}
