using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;
using Oscova.Agents.Conversations;

namespace Oscova.Agents.Providers;

/// <summary>
/// Exposes a single <c>search_tools</c> entry point instead of the agent's full tool set.
/// Discovered tools are added to the live run; previously called tools return from the current session's history.
/// </summary>
public sealed class ToolSearchProvider : AIContextProvider
{
    private const int SearchResultLimit = 5;
    /// <summary>The callable identity of the tool that searches and loads available tools.</summary>
    public const string SearchToolName = "search_tools";
    /// <summary>Default instruction used when the host has no custom tool-search prompt.</summary>
    public const string DefaultInstructions =
        """
        Some tools are already available, and the rest of your tool set is hidden behind `search_tools`.
        Use these exact search keywords: `agenttask` for recurring or scheduled work, `visualization` for interactive displays, `encryptedfile` for encrypted files, and `tabs` for browser tabs.
        Other searchable families are `pdf`, `datetime`, `skillauthoring`, `skillregistry`, `mcpregistry`, `notification`, `logs`, `chathistory`, and `wait`.
        When you need a capability that is not already available, call `search_tools` with a short description.
        When the user asks for a kind of tool you do not see, search for it before working around it with other tools.
        After matching tools are loaded, call the newly available tool that best fits the task.
        Only search for what is needed, when it is needed.
        """;

    private readonly IReadOnlyList<string> _allowedToolNames;
    private readonly HashSet<string> _pinnedToolNames;
    private readonly string _instructions;
    private readonly AgentToolResolver _resolver;
    private readonly IToolSearchStrategy _strategy;
    private readonly AgentToolCollection _tools;
    private readonly Lock _gate = new();
    private readonly AIFunction _searchTool;

    /// <summary>Initializes a provider that hides allowed tools behind lexical discovery.</summary>
    public ToolSearchProvider(
        IEnumerable<string> allowedToolNames,
        IEnumerable<string> pinnedToolNames,
        AgentToolCollection tools,
        AgentToolResolver resolver,
        IToolSearchStrategy strategy,
        string? instructions = null)
    {
        _allowedToolNames = [.. allowedToolNames];
        _pinnedToolNames = new HashSet<string>(pinnedToolNames, StringComparer.OrdinalIgnoreCase);
        _instructions = instructions ?? DefaultInstructions;
        _tools = tools;
        _resolver = resolver;
        _strategy = strategy;
        _searchTool = AIFunctionFactory.Create(
            SearchTools,
            name: SearchToolName,
            description: "Search for tools by capability and load the matches so you can call them.");
    }

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var exposedTools = new List<AITool> { _searchTool };
        // Runs can rebuild the agent, so only this thread's retained calls restore tool visibility.
        List<ChatMessage> messages = context?.Session is { } session && session.TryGetInMemoryChatHistory(out var history)
            ? [.. history]
            : [];
        var restoredNames = messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Select(call => call.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // An approval resume rebuilds the tool list mid-turn, so this turn's search results stay loaded until the next user text.
        messages.AddRange(context?.AIContext.Messages ?? []);
        var turnStart = messages.FindLastIndex(message => message.IsSubmittedRequest() && message.Contents.OfType<TextContent>().Any());
        var turnSearches = messages.Skip(turnStart + 1).SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Where(call => call.Name == SearchToolName);
        foreach (var search in turnSearches)
        {
            if (search.Arguments?.TryGetValue("query", out var value) == true && value?.ToString() is { } query)
            {
                restoredNames.UnionWith(_strategy.Search(query, BuildCandidates(), SearchResultLimit));
            }
        }

        var exposedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _allowedToolNames.Where(name => _pinnedToolNames.Contains(name) || restoredNames.Contains(name))
            .OrderByDescending(_pinnedToolNames.Contains).ThenBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var tool = _resolver.Resolve(name);
            if (tool is not null && exposedNames.Add(tool.Name))
            {
                exposedTools.Add(tool);
            }
        }

        return new ValueTask<AIContext>(new AIContext
        {
            Instructions = _instructions,
            Tools = exposedTools
        });
    }

    private string SearchTools(string query)
    {
        var candidates = BuildCandidates();
        var matchNames = _strategy.Search(query, candidates, SearchResultLimit);
        if (matchNames.Count == 0)
        {
            return $"No tools matched \"{query}\".";
        }

        var liveTools = FunctionInvokingChatClient.CurrentContext?.Options?.Tools;
        var lines = new List<string>();
        lock (_gate)
        {
            foreach (var name in matchNames)
            {
                if (_pinnedToolNames.Contains(name))
                {
                    continue;
                }

                var tool = _resolver.Resolve(name);
                if (tool is null)
                {
                    continue;
                }

                if (liveTools is not null && tool is AIFunction function && !ContainsFunction(liveTools, function.Name))
                {
                    liveTools.Add(tool);
                }

                lines.Add($"- {name}: {_tools.Find(name)?.Description}");
            }
        }

        return lines.Count == 0
            ? $"No tools matched \"{query}\"."
            : "Loaded tools (now callable):\n" + string.Join("\n", lines);
    }

    /// <summary>Returns effective unpinned functions available through tool discovery.</summary>
    public IReadOnlyList<AIFunction> GetSearchableTools()
    {
        var functions = new List<AIFunction>();
        foreach (var name in _allowedToolNames)
        {
            if (_pinnedToolNames.Contains(name)) { continue; }
            if (_resolver.Resolve(name) is AIFunction function)
            {
                functions.Add(function);
            }
        }
        return functions;
    }

    private IReadOnlyList<ToolSearchCandidate> BuildCandidates()
    {
        var candidates = new List<ToolSearchCandidate>();
        // Discovery and inspection use the same effective functions and argument metadata.
        foreach (var function in GetSearchableTools())
        {
            candidates.Add(new ToolSearchCandidate(
                function.Name,
                function.Description,
                GetArgumentNames(function.JsonSchema)));
        }
        return candidates;
    }

    private static IReadOnlyList<string> GetArgumentNames(JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var property in properties.EnumerateObject())
        {
            names.Add(property.Name);
        }

        return names;
    }

    private static bool ContainsFunction(IList<AITool> tools, string functionName)
    {
        return tools.Any(tool =>
            tool is AIFunction function &&
            string.Equals(function.Name, functionName, StringComparison.OrdinalIgnoreCase));
    }
}
