namespace Oscova.Agents.Core.ApiContracts.Prompts;

/// <summary>Groups the editable prompt files exposed by the settings API.</summary>
public sealed class PromptCollection
{
    public PromptDocument BrowserUse { get; set; } = new();
    public PromptDocument Compaction { get; set; } = new();
    public PromptDocument Memory { get; set; } = new();
    public PromptDocument Environment { get; set; } = new();
    public PromptDocument Library { get; set; } = new();
    public PromptDocument ToolSearch { get; set; } = new();
    public PromptDocument TodoProvider { get; set; } = new();
    public PromptDocument Subagents { get; set; } = new();
    public PromptDocument Skills { get; set; } = new();
    public PromptDocument AvatarMarkup { get; set; } = new();
    public PromptDocument ChatContinuation { get; set; } = new();
    public PromptDocument GoalJudge { get; set; } = new();
    public PromptDocument GoalContinuation { get; set; } = new();
    public PromptDocument ChatTitle { get; set; } = new();
    public PromptDocument ToolFailureRecovery { get; set; } = new();
}
