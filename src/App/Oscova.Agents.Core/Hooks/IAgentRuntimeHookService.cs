using Oscova.Agents.Hooks;

namespace Oscova.Agents.Core.Hooks;

/// <summary>
/// Dispatches configured external hooks for chat session, prompt, and tool runtime events.
/// </summary>
public interface IAgentRuntimeHookService
{
    /// <summary>
    /// Dispatches an observe-only session-start hook.
    /// </summary>
    Task<HookDispatchResult> DispatchSessionStartAsync(SessionHookContext context, CancellationToken ct = default);

    /// <summary>
    /// Dispatches a blocking user-prompt hook.
    /// </summary>
    Task<HookDispatchResult> DispatchUserPromptSubmitAsync(PromptHookContext context, CancellationToken ct = default);

    /// <summary>
    /// Dispatches a blocking pre-tool-use hook.
    /// </summary>
    Task<HookDispatchResult> DispatchPreToolUseAsync(ToolHookContext context, CancellationToken ct = default);

    /// <summary>
    /// Dispatches an observe-only post-tool-use hook.
    /// </summary>
    Task DispatchPostToolUseAsync(ToolHookContext context, CancellationToken ct = default);
}