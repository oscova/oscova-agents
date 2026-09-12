using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;
using Oscova.Agents.Utils;

namespace Oscova.Agents.Llm;

/// <summary>
/// Estimates token usage for the prompt surface used by context-window preflight checks.
/// </summary>
public static class ContextWindowTokenEstimator
{
    /// <summary>
    /// Estimates tokens for free-form prompt text.
    /// </summary>
    public static int EstimateText(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? 0 : AgentTokenCounter.CountTokens(text);
    }

    /// <summary>
    /// Estimates tokens for a chat message sequence.
    /// </summary>
    public static int EstimateMessages(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append(message.Role).Append(':').AppendLine();
            AppendMessageContent(builder, message);
        }

        return EstimateText(builder.ToString());
    }

    /// <summary>
    /// Estimates tokens for a tool declaration list, including names, descriptions, and schemas.
    /// </summary>
    public static int EstimateTools(IEnumerable<AITool> tools)
    {
        var builder = new StringBuilder();
        foreach (var tool in tools)
        {
            builder.AppendLine(tool.Name);
            AppendIfPresent(builder, tool.Description);

            if (tool is AIFunctionDeclaration function)
            {
                AppendIfPresent(builder, function.JsonSchema.ToString());
            }
        }

        return EstimateText(builder.ToString());
    }

    private static void AppendMessageContent(StringBuilder builder, ChatMessage message)
    {
        AppendIfPresent(builder, message.Text);

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionCallContent functionCall:
                    AppendIfPresent(builder, functionCall.Name);
                    AppendIfPresent(builder, AgentJsonText.SerializeValue(functionCall.Arguments));
                    break;

                case FunctionResultContent functionResult:
                    AppendIfPresent(builder, functionResult.CallId);
                    AppendIfPresent(builder, AgentJsonText.SerializeValue(functionResult.Result));
                    break;
            }
        }
    }

    private static void AppendIfPresent(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine(value);
    }
}
