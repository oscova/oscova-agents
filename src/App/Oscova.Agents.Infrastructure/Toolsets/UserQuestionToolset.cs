using System.ComponentModel;
using Microsoft.Extensions.AI;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Harness;
using Oscova.Agents.Providers;

namespace Oscova.Agents.Infrastructure.Toolsets;

/// <summary>Posts clarification questions and finishes the current web turn.</summary>
[DisplayName(UserQuestions.ToolsetName)]
public sealed class UserQuestionToolset(RunContextAccessor runContextAccessor) : IAgentToolset
{
    /// <summary>Posts one question batch without retaining a waiting execution.</summary>
    [DisplayName(UserQuestions.ToolName)]
    [AgentTool]
    [Description("""
        Ask for preferences or missing information with suggested answers or free text.
        Only the directly selected web conversation agent can use this tool.
        Never use for permission grants.
        Call alone, with no other tools in the same batch. Posting questions ends this turn.
        Submitted answers arrive as a new user message. Dismissal does not start another turn.
        """)]
    public string AskUserQuestion(
        [Description("""
            Questions: 1-4 items.
            All arrays and items must be non-null.
            Question IDs: nonblank, max 64 characters.
            Question IDs must be unique within the batch (case-sensitive).
            Header: nonblank, max 24 characters including spaces.
            Text: nonblank, max 1000 characters.
            Options: [] for free text only, otherwise 2-4 items.
            Option IDs: nonblank, max 64 characters.
            Option IDs must be unique within their question (case-sensitive).
            Label: nonblank, max 100 characters.
            Description: optional, max 300 characters.
            Recommended=true: at most one option per question.
            Do not add Other: the UI provides custom text input.
            A recommendation is not a submitted answer.
            """)]
        IReadOnlyList<UserQuestions.Question> questions)
    {
        UserQuestions.Validate(questions);
        var invocation = FunctionInvokingChatClient.CurrentContext;
        var run = runContextAccessor.Current;
        if (invocation is null || invocation.FunctionCount != 1)
        {
            throw new ToolFailureException("Call Ask User Question alone in its tool batch.");
        }
        // A subagent shares its parent identity, so the worker check is what keeps questions with the parent.
        if (DelegatedWorkState.CurrentWorker is not null || run?.QuestionAgentId is null || run.QuestionAgentId != run.CurrentAgentId)
        {
            throw new ToolFailureException("Questions are available only in the active web conversation.");
        }

        // Retain the receipt before stopping further model iterations.
        invocation.Terminate = true;
        return UserQuestions.PostedReceipt;
    }
}
