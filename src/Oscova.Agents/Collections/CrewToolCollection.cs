using Microsoft.Extensions.AI;
using Oscova.Agents.Policies;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Oscova.Agents.Collections;

/// <summary>
/// Stores reflected and externally supplied tools with normalized lookup metadata.
/// </summary>
/// <remarks>
/// Tool names are treated case-insensitively for lookup while preserving each function name value for execution.
/// </remarks>
public class CrewToolCollection : ICollection<AIFunction>
{
    private readonly Lock _gate = new();
    private readonly List<AIFunction> _tools = [];
    private readonly Dictionary<string, AIFunction> _toolsByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _serverNamesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _originalNamesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolApprovalMode> _defaultApprovalModesByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NetworkScope> _declaredNetworkScopeByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NetworkScope> _networkScopeByQualifiedName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates an empty tool collection.
    /// </summary>
    public CrewToolCollection() { }

    /// <summary>
    /// Creates a tool collection populated with existing AI functions.
    /// </summary>
    /// <param name="tools">The tools to add using their existing qualified names.</param>
    public CrewToolCollection(IEnumerable<AIFunction> tools)
    {
        AddRange(tools);
    }

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tools.Count;
            }
        }
    }
    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <summary>
    /// Adds a tool using its qualified name as the lookup key.
    /// </summary>
    /// <param name="item">The AI function to add.</param>
    public void Add(AIFunction item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (qualifier, tool) = ToolNaming.SplitQualifiedName(item.Name);
        AddCore(item, qualifier, tool);
    }

    /// <summary>
    /// Adds a tool with explicit external-server metadata.
    /// </summary>
    /// <param name="item">The AI function to add.</param>
    /// <param name="serverName">The external server or provider name that supplied the tool.</param>
    /// <param name="originalName">The tool name before framework qualification.</param>
    /// <param name="networkScope">The tool's declared network scope.</param>
    public void Add(
        AIFunction item,
        string serverName,
        string originalName,
        NetworkScope networkScope = NetworkScope.None)
    {
        ArgumentNullException.ThrowIfNull(item);
        AddCore(
            item,
            ToolNaming.NormalizeQualifier(serverName),
            ToolNaming.NormalizeToolName(originalName),
            networkScope: networkScope);
    }

    private void AddCore(
        AIFunction item,
        string serverName,
        string originalName,
        ToolApprovalMode defaultApprovalMode = ToolApprovalMode.Default,
        NetworkScope networkScope = NetworkScope.None)
    {
        lock (_gate)
        {
            if (_toolsByQualifiedName.ContainsKey(item.Name))
            {
                throw new InvalidOperationException($"A tool named '{item.Name}' is already registered.");
            }

            _tools.Add(item);
            _toolsByQualifiedName[item.Name] = item;
            _serverNamesByQualifiedName[item.Name] = serverName;
            _originalNamesByQualifiedName[item.Name] = originalName;
            _defaultApprovalModesByQualifiedName[item.Name] = defaultApprovalMode;
            _declaredNetworkScopeByQualifiedName[item.Name] = networkScope;
            _networkScopeByQualifiedName[item.Name] = networkScope;
        }
    }

    /// <summary>
    /// Replaces all tools and metadata from another collection in one publish step.
    /// </summary>
    /// <param name="source">The collection snapshot to publish.</param>
    internal void ReplaceWith(CrewToolCollection source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(this, source))
        {
            return;
        }

        lock (source._gate)
        {
            lock (_gate)
            {
                ClearCore();
                _tools.AddRange(source._tools);
                CopyDictionary(source._toolsByQualifiedName, _toolsByQualifiedName);
                CopyDictionary(source._serverNamesByQualifiedName, _serverNamesByQualifiedName);
                CopyDictionary(source._originalNamesByQualifiedName, _originalNamesByQualifiedName);
                CopyDictionary(source._defaultApprovalModesByQualifiedName, _defaultApprovalModesByQualifiedName);
                CopyDictionary(source._declaredNetworkScopeByQualifiedName, _declaredNetworkScopeByQualifiedName);
                CopyDictionary(source._networkScopeByQualifiedName, _networkScopeByQualifiedName);
            }
        }
    }

    private static void CopyDictionary<TKey, TValue>(
        Dictionary<TKey, TValue> source,
        Dictionary<TKey, TValue> destination)
        where TKey : notnull
    {
        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value;
        }
    }

    /// <summary>
    /// Gets the default approval mode declared by a reflected tool method.
    /// </summary>
    /// <param name="qualifiedName">The normalized qualified tool name.</param>
    /// <returns>The declared approval mode, or <see cref="ToolApprovalMode.Default"/> when none was recorded.</returns>
    public ToolApprovalMode GetDefaultApprovalMode(string qualifiedName)
    {
        lock (_gate)
        {
            return _defaultApprovalModesByQualifiedName.TryGetValue(qualifiedName, out var mode)
                ? mode
                : ToolApprovalMode.Default;
        }
    }

    /// <summary>
    /// Gets the tool's declared network scope.
    /// </summary>
    /// <param name="qualifiedName">The normalized qualified tool name.</param>
    /// <returns><see cref="NetworkScope.None"/> when the scope is undeclared.</returns>
    public NetworkScope GetNetworkScope(string qualifiedName)
    {
        lock (_gate)
        {
            return _networkScopeByQualifiedName.TryGetValue(qualifiedName, out var scope)
                ? scope
                : NetworkScope.None;
        }
    }

    /// <summary>
    /// Gets the tool's source-declared network scope before any user governance override.
    /// </summary>
    /// <param name="qualifiedName">The normalized qualified tool name.</param>
    /// <returns><see cref="NetworkScope.None"/> when the scope is undeclared.</returns>
    public NetworkScope GetDeclaredNetworkScope(string qualifiedName)
    {
        lock (_gate)
        {
            return _declaredNetworkScopeByQualifiedName.TryGetValue(qualifiedName, out var scope)
                ? scope
                : NetworkScope.None;
        }
    }

    /// <summary>
    /// Updates the effective network scope for an already registered tool.
    /// </summary>
    /// <param name="qualifiedName">The qualified tool name to update.</param>
    /// <param name="networkScope">The effective network scope to store.</param>
    /// <returns><see langword="true"/> when the tool exists and was updated.</returns>
    public bool TrySetNetworkScope(string qualifiedName, NetworkScope networkScope)
    {
        var normalizedName = ToolNaming.NormalizeQualifier(qualifiedName);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_toolsByQualifiedName.ContainsKey(normalizedName))
            {
                return false;
            }

            _networkScopeByQualifiedName[normalizedName] = networkScope;
            return true;
        }
    }

    /// <summary>
    /// Reflects public declared methods from a toolset and adds them as AI functions.
    /// </summary>
    /// <param name="toolSet">The trusted toolset instance to reflect.</param>
    /// <param name="origin">The optional plugin origin used to qualify reflected tool names.</param>
    public void Add(object toolSet, string? origin = null)
    {
        ArgumentNullException.ThrowIfNull(toolSet);
        var type = toolSet.GetType();
        var owner = ToolNaming.GetToolsetOwner(type);

        foreach (var methodInfo in type.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var displayName = methodInfo.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
            var baseName = string.IsNullOrWhiteSpace(displayName) ? methodInfo.Name : displayName;
            var toolName = ToolNaming.NormalizeToolName(baseName);

            if (string.IsNullOrEmpty(toolName))
            {
                throw new InvalidOperationException(
                    $"Tool name derived from method '{methodInfo.Name}' cannot be empty.");
            }

            var qualifiedName = ToolNaming.BuildToolsetQualifiedName(origin, owner, toolName);
            var function = AIFunctionFactory.Create(
                methodInfo,
                methodInfo.IsStatic ? null : toolSet,
                new AIFunctionFactoryOptions
                {
                    Name = qualifiedName,
                    Description = methodInfo.GetCustomAttribute<DescriptionAttribute>()?.Description
                                  ?? string.Empty,
                });

            var defaultApprovalMode =
                methodInfo.GetCustomAttribute<ToolPolicyAttribute>()?.ApprovalMode
                ?? ToolApprovalMode.Default;
            var networkScope = methodInfo.GetCustomAttribute<ToolNetworkScopeAttribute>()?.Scope ?? NetworkScope.None;

            AddCore(
                function,
                string.IsNullOrWhiteSpace(origin) ? owner : ToolNaming.NormalizeQualifier(origin),
                toolName,
                defaultApprovalMode,
                networkScope);
        }
    }

    /// <summary>
    /// Adds a sequence of tools using each tool's existing qualified name.
    /// </summary>
    /// <param name="items">The tools to add.</param>
    public void AddRange(IEnumerable<AIFunction> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }

    /// <summary>
    /// Creates AI functions from a toolset without adding them to a collection.
    /// </summary>
    /// <param name="toolSet">The trusted toolset instance to reflect.</param>
    /// <returns>AI functions named from the toolset type and public declared method metadata.</returns>
    public static IEnumerable<AIFunction> CreateToolsFromToolset(object toolSet)
    {
        ArgumentNullException.ThrowIfNull(toolSet);
        var toReturn = new List<AIFunction>();
        var type = toolSet.GetType();
        var owner = ToolNaming.GetToolsetOwner(type);

        foreach (var methodInfo in type.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var displayName = methodInfo.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
            var baseName = string.IsNullOrWhiteSpace(displayName) ? methodInfo.Name : displayName;
            var toolName = ToolNaming.NormalizeToolName(baseName);

            if (string.IsNullOrEmpty(toolName))
            {
                throw new InvalidOperationException(
                    $"Tool name derived from method '{methodInfo.Name}' cannot be empty.");
            }

            var qualifiedName = ToolNaming.BuildToolsetQualifiedName(null, owner, toolName);

            var function = AIFunctionFactory.Create(
                methodInfo,
                methodInfo.IsStatic ? null : toolSet,
                new AIFunctionFactoryOptions
                {
                    Name = qualifiedName,
                    Description = methodInfo.GetCustomAttribute<DescriptionAttribute>()?.Description
                                  ?? string.Empty,
                });

            toReturn.Add(function);
        }

        return toReturn;
    }

    /// <summary>
    /// Finds a tool by its qualified name.
    /// </summary>
    /// <param name="qualifiedName">The normalized qualified tool name.</param>
    /// <returns>The matching tool, or <see langword="null"/> when no tool is registered.</returns>
    public AIFunction? Find(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return null;
        }
        lock (_gate)
        {
            return _toolsByQualifiedName.TryGetValue(qualifiedName, out var fn) ? fn : null;
        }
    }

    /// <summary>
    /// Finds tools whose qualified names appear in the provided list.
    /// </summary>
    /// <param name="names">The qualified names to resolve.</param>
    /// <returns>The registered tools that matched the requested names.</returns>
    public IReadOnlyList<AIFunction> FindAll(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var nameSet = new HashSet<string>(
            names.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            var results = new List<AIFunction>();
            foreach (var name in nameSet)
            {
                if (_toolsByQualifiedName.TryGetValue(name, out var tool))
                {
                    results.Add(tool);
                }
            }

            return results;
        }
    }

    /// <summary>
    /// Finds tools whose qualified names start with the provided prefix.
    /// </summary>
    /// <param name="prefix">The qualified-name prefix to match.</param>
    /// <returns>The matching tools, or an empty list when the prefix is blank.</returns>
    public IReadOnlyList<AIFunction> FindAll(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return [];
        }
        lock (_gate)
        {
            return [.. _tools.Where(t =>
                t.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
        }
    }

    /// <summary>
    /// Gets all tools supplied by a normalized server or provider name.
    /// </summary>
    /// <param name="serverName">The external server or reflected toolset qualifier.</param>
    /// <returns>The tools registered for the server.</returns>
    public IReadOnlyList<AIFunction> GetToolsByServer(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return [];
        }
        var normalized = ToolNaming.NormalizeQualifier(serverName);
        lock (_gate)
        {
            return [.. _tools.Where(t =>
                string.Equals(
                    _serverNamesByQualifiedName.GetValueOrDefault(t.Name),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))];
        }
    }

    /// <summary>
    /// Gets the recorded server name for a tool.
    /// </summary>
    /// <param name="tool">The tool whose server metadata should be returned.</param>
    /// <returns>The normalized server name, or an empty string when none is recorded.</returns>
    public string GetServerName(AIFunction tool) =>
        GetServerName(tool.Name);

    /// <summary>
    /// Gets the recorded server name for a qualified tool name.
    /// </summary>
    /// <param name="qualifiedName">The qualified tool name.</param>
    /// <returns>The normalized server name, or an empty string when none is recorded.</returns>
    public string GetServerName(string qualifiedName)
    {
        lock (_gate)
        {
            return _serverNamesByQualifiedName.GetValueOrDefault(qualifiedName, string.Empty);
        }
    }

    /// <summary>
    /// Gets the original unqualified name for a tool.
    /// </summary>
    /// <param name="tool">The tool whose original-name metadata should be returned.</param>
    /// <returns>The normalized original name, or an empty string when none is recorded.</returns>
    public string GetOriginalName(AIFunction tool) =>
        GetOriginalName(tool.Name);

    /// <summary>
    /// Gets the original unqualified name for a qualified tool name.
    /// </summary>
    /// <param name="qualifiedName">The qualified tool name.</param>
    /// <returns>The normalized original name, or an empty string when none is recorded.</returns>
    public string GetOriginalName(string qualifiedName)
    {
        lock (_gate)
        {
            return _originalNamesByQualifiedName.GetValueOrDefault(qualifiedName, string.Empty);
        }
    }

    /// <inheritdoc />
    public bool Contains(AIFunction item)
    {
        lock (_gate)
        {
            return item is not null && _toolsByQualifiedName.ContainsKey(item.Name);
        }
    }

    /// <inheritdoc />
    public void CopyTo(AIFunction[] array, int arrayIndex)
    {
        lock (_gate)
        {
            _tools.CopyTo(array, arrayIndex);
        }
    }

    /// <inheritdoc />
    public bool Remove(AIFunction item)
    {
        if (item is null)
        {
            return false;
        }
        return RemoveByName(item.Name);
    }

    /// <summary>
    /// Removes a tool and its metadata by qualified name.
    /// </summary>
    /// <param name="qualifiedName">The qualified tool name to remove.</param>
    /// <returns><see langword="true"/> when a tool was removed.</returns>
    public bool Remove(string qualifiedName)
    {
        return !string.IsNullOrWhiteSpace(qualifiedName) && RemoveByName(qualifiedName);
    }

    private bool RemoveByName(string qualifiedName)
    {
        lock (_gate)
        {
            if (!_toolsByQualifiedName.Remove(qualifiedName, out var fn))
            {
                return false;
            }

            _tools.Remove(fn);
            _serverNamesByQualifiedName.Remove(qualifiedName);
            _originalNamesByQualifiedName.Remove(qualifiedName);
            _defaultApprovalModesByQualifiedName.Remove(qualifiedName);
            _declaredNetworkScopeByQualifiedName.Remove(qualifiedName);
            _networkScopeByQualifiedName.Remove(qualifiedName);
            return true;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            ClearCore();
        }
    }

    private void ClearCore()
    {
        _tools.Clear();
        _toolsByQualifiedName.Clear();
        _serverNamesByQualifiedName.Clear();
        _originalNamesByQualifiedName.Clear();
        _defaultApprovalModesByQualifiedName.Clear();
        _declaredNetworkScopeByQualifiedName.Clear();
        _networkScopeByQualifiedName.Clear();
    }

    /// <inheritdoc />
    public IEnumerator<AIFunction> GetEnumerator()
    {
        lock (_gate)
        {
            return new List<AIFunction>(_tools).GetEnumerator();
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
