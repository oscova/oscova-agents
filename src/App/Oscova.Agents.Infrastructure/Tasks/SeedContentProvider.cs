using System.Reflection;
using Oscova.Agents.Core;
using Oscova.Agents.Core.Conversation;
using Microsoft.Agents.AI;
using Oscova.Agents.Memory;
using Oscova.Agents.Providers;
using Oscova.Agents.Reducers;

namespace Oscova.Agents.Infrastructure.Tasks;

#pragma warning disable MAAI001 // Goal defaults use the released loop evaluator.

/// <summary>
/// Loads invariant seed instruction markdown embedded in this assembly.
/// </summary>
public static class SeedContentProvider
{
    private static readonly Assembly ContentAssembly = typeof(SeedContentProvider).Assembly;
    private const string DefaultSeedCulture = "en-US";

    /// <summary>Returns the host topic defaults shared by runtime composition and editing.</summary>
    public static IReadOnlyDictionary<string, string> GetPromptDefaults() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [PromptFileNames.CompactionPrompt] = SummarizingChatReducer.DefaultSummarizationPrompt,
        [PromptFileNames.MemorySystemPrompt] = MemoryContextProvider.DefaultMemoryPrompt,
        // The host seed includes guidance for its named workspaces.
        [PromptFileNames.EnvironmentPrompt] = GetInstruction(PromptFileNames.EnvironmentPrompt),
        [PromptFileNames.LibraryPrompt] = LibraryContextProvider.DefaultInstructions,
        [PromptFileNames.ToolSearchPrompt] = ToolSearchProvider.DefaultInstructions,
        [PromptFileNames.TodoProviderPrompt] = GetInstruction(PromptFileNames.TodoProviderPrompt),
        [PromptFileNames.SubagentsPrompt] = AgentAsToolProvider.DefaultInstructions,
        [PromptFileNames.SkillsPrompt] = SkillsPromptDefaults.DefaultInstructions,
        [PromptFileNames.AvatarMarkupPrompt] = GetInstruction(PromptFileNames.AvatarMarkupPrompt),
        [PromptFileNames.ChatContinuationPrompt] = GetInstruction(PromptFileNames.ChatContinuationPrompt),
        [PromptFileNames.GoalJudgePrompt] = AIJudgeLoopEvaluator.DefaultInstructions,
        [PromptFileNames.GoalContinuationPrompt] = AIJudgeLoopEvaluator.DefaultFeedbackMessageTemplate,
        [PromptFileNames.ChatTitlePrompt] = GetInstruction(PromptFileNames.ChatTitlePrompt),
        [PromptFileNames.ToolFailureRecoveryPrompt] = GetInstruction(PromptFileNames.ToolFailureRecoveryPrompt)
    };

    /// <summary>
    /// Returns the seeded instruction text for a prompt or task slug.
    /// </summary>
    public static string GetInstruction(string slug)
    {
        if (string.Equals(slug, PromptFileNames.SkillsPrompt, StringComparison.OrdinalIgnoreCase))
        {
            // Keep the editable skills prompt aligned with the framework prompt fallback exactly.
            return SkillsPromptDefaults.DefaultInstructions;
        }

        return ReadEmbedded(slug.EndsWith(".enc", StringComparison.OrdinalIgnoreCase) ? slug[..^4] : slug)
            ?? throw new InvalidOperationException(
                $"Seed content '{slug}' is missing from the default seed instructions.");
    }

    private static string? ReadEmbedded(string slug)
    {
        // MSBuild converts culture folder hyphens to underscores in manifest resource names.
        // Accept slash-separated paths (e.g. "prompts/compaction.md") — callers pass the full file name.
        var manifestCulture = DefaultSeedCulture.Replace('-', '_');
        var normalizedSlug = slug.Replace('/', '.');
        var suffix = $"SeedContent.{manifestCulture}.{normalizedSlug}";
        var resourceName = ContentAssembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return null;
        }

        using var stream = ContentAssembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd();
    }
}
