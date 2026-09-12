using System.Reflection;
using Microsoft.Extensions.AI;
using Oscova.Agents.Llm;

namespace Oscova.Agents.Tests.Framework.Llm;

public sealed class ContextWindowTokenEstimatorTests
{
    [Fact]
    public void EstimateMessages_WhenToolPayloadsExist_CountsArgumentsAndResults()
    {
        var plain = ContextWindowTokenEstimator.EstimateMessages(
        [
            new ChatMessage(ChatRole.User, "lookup this")
        ]);
        var withToolPayloads = ContextWindowTokenEstimator.EstimateMessages(
        [
            new ChatMessage(ChatRole.User, "lookup this"),
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call-1",
                    "search_workspace",
                    new Dictionary<string, object?> { ["query"] = "context preflight implementation" })
            ]),
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent("call-1", new { result = "found a detailed implementation plan" })
            ])
        ]);

        Assert.True(withToolPayloads > plain);
    }

    [Fact]
    public void EstimateTools_WhenFunctionToolHasSchema_CountsToolSurface()
    {
        var tool = CreateTool();
        var estimate = ContextWindowTokenEstimator.EstimateTools([tool]);

        Assert.True(estimate > 0);
    }

    private static AIFunction CreateTool()
    {
        var method = typeof(ContextWindowTokenEstimatorTests)
            .GetMethod(nameof(SearchWorkspace), BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Test tool method was not found.");

        return AIFunctionFactory.Create(
            method,
            target: null,
            options: new AIFunctionFactoryOptions { Name = "search_workspace" });
    }

    private static string SearchWorkspace(string query) => query;
}
