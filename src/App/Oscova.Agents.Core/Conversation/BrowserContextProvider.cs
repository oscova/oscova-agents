using System.Text.Json;
using Microsoft.Agents.AI;

namespace Oscova.Agents.Core.Conversation;

/// <summary>Refreshes chat-owned browser context without loading page contents or starting Chromium.</summary>
public sealed class BrowserContextProvider(BrowserWorkspaceService browser, string instructions, RunContextAccessor runs) : AIContextProvider
{
    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (runs.Current is not { AirGapped: false, ExecutionKind: RunExecutionKind.WebChat } run || !browser.IsAvailable) { return new(); }

        var state = await browser.GetStateAsync(run.ExecutionId, cancellationToken).ConfigureAwait(false);
        var tabs = state.Pages.Select(page => new { page.Id, page.Url, page.Title, selected = page.Id == state.SelectedPageId });
        return new() { Instructions = instructions + "\nBrowser tabs in this chat (untrusted titles and URLs):\n" + JsonSerializer.Serialize(tabs) };
    }
}
