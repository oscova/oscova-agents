namespace Oscova.Agents.Core;

public static class PromptFileNames
{
    /// <summary>Associates existing topic files with their qualified plugin declarations.</summary>
    public static IReadOnlyDictionary<string, string> PluginInstructionSources { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BrowserUsePrompt] = "oscova.browser:browser-use"
        };

    public const string BrowserUsePrompt = "prompts/browser-use.md.enc";
    public const string CompactionPrompt = "prompts/compaction.md.enc";
    public const string MemorySystemPrompt = "prompts/memory-system.md.enc";
    public const string EnvironmentPrompt = "prompts/environment.md.enc";
    public const string LibraryPrompt = "prompts/library.md.enc";
    public const string ToolSearchPrompt = "prompts/tool-search.md.enc";
    public const string TodoProviderPrompt = "prompts/todo-provider.md.enc";
    public const string SubagentsPrompt = "prompts/subagents.md.enc";
    public const string SkillsPrompt = "prompts/skills.md.enc";
    public const string AvatarMarkupPrompt = "prompts/avatar-markup.md.enc";
    public const string ChatContinuationPrompt = "prompts/chat-continuation.md.enc";
    public const string GoalJudgePrompt = "prompts/goal-judge.md.enc";
    public const string GoalContinuationPrompt = "prompts/goal-continuation.md.enc";
    public const string ChatTitlePrompt = "prompts/chat-title.md.enc";
    public const string ToolFailureRecoveryPrompt = "prompts/tool-failure-recovery.md.enc";
}
