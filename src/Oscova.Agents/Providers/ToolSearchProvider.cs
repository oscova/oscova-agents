using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Oscova.Agents.Collections;

namespace Oscova.Agents.Providers;

/// <summary>
/// Exposes a single <c>search_tools</c> entry point instead of the agent's full tool set.
/// Discovered tools are added to the live run and remembered for the cached agent instance.
/// </summary>
public sealed class ToolSearchProvider : AIContextProvider
{
    private const int SearchResultLimit = 5;
    /// <summary>Default instruction used when the host has no custom tool-search prompt.</summary>
    public const string DefaultInstructions =
        """
        Some tools are already available, and the rest of your tool set is hidden behind `search_tools`.
        When you need a capability that is not already available, call `search_tools` with a short description.
        After matching tools are loaded, call the newly available tool that best fits the task.
        Only search for what is needed, when it is needed.
        """;

    private readonly IReadOnlyList<string> _allowedToolNames;
    private readonly HashSet<string> _pinnedToolNames;
    private readonly string _instructions;
    private readonly AgentToolResolver _resolver;
    private readonly IToolSearchStrategy _strategy;
    private readonly CrewToolCollection _tools;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _unlocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly AIFunction _searchTool;

    /// <summary>Initializes a provider that hides allowed tools behind lexical discovery.</summary>
    public ToolSearchProvider(
        IEnumerable<string> allowedToolNames,
        IEnumerable<string> pinnedToolNames,
        CrewToolCollection tools,
        AgentToolResolver resolver,
        IToolSearchStrategy strategy,
        string? instructions = null)
    {
        _allowedToolNames = [.. allowedToolNames];
        _pinnedToolNames = new HashSet<string>(pinnedToolNames, StringComparer.OrdinalIgnoreCase);
        _instructions = string.IsNullOrWhiteSpace(instructions) ? DefaultInstructions : instructions;
        _tools = tools;
        _resolver = resolver;
        _strategy = strategy;
        _searchTool = AIFunctionFactory.Create(
            SearchTools,
            name: "search_tools",
            description: "Search for tools by capability and load the matches so you can call them.");
    }

    /// <inheritdoc />
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var exposedTools = new List<AITool> { _searchTool };
        // Only _unlocked is shared mutable state; pinned and allowed names are constructor snapshots.
        var exposedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _allowedToolNames.Where(_pinnedToolNames.Contains).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var tool = _resolver.Resolve(name);
            if (tool is not null && exposedNames.Add(tool.Name))
            {
                exposedTools.Add(tool);
            }
        }

        lock (_gate)
        {
            foreach (var name in _unlocked.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                if (_pinnedToolNames.Contains(name))
                {
                    continue;
                }

                var tool = _resolver.Resolve(name);
                if (tool is not null && exposedNames.Add(tool.Name))
                {
                    exposedTools.Add(tool);
                }
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

                _unlocked.Add(name);
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

    private IReadOnlyList<ToolSearchCandidate> BuildCandidates()
    {
        var candidates = new List<ToolSearchCandidate>();
        foreach (var name in _allowedToolNames)
        {
            if (_pinnedToolNames.Contains(name))
            {
                continue;
            }

            if (_resolver.Resolve(name) is not AIFunction function)
            {
                continue;
            }

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
