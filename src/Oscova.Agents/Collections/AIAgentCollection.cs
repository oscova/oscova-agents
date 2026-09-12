using Microsoft.Agents.AI;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Oscova.Agents.Collections;

/// <summary>
/// Stores built chat agents by identifier for reuse across agent runs.
/// </summary>
/// <remarks>
/// Enumeration returns a snapshot so callers can iterate without holding the collection lock.
/// </remarks>
public class AIAgentCollection : ICollection<ChatClientAgent>
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ChatClientAgent> _agents = [];

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _agents.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <summary>
    /// Adds or replaces a cached agent using its framework identifier.
    /// </summary>
    /// <param name="item">The agent to cache.</param>
    public void Add(ChatClientAgent item)
    {
        lock (_gate)
        {
            _agents[item.Id] = item;
        }
    }

    /// <summary>
    /// Returns the existing cached agent with the same identifier or stores the supplied one.
    /// </summary>
    /// <param name="item">The agent to cache if no existing agent is present.</param>
    /// <returns>The cached agent for the supplied identifier.</returns>
    public ChatClientAgent GetOrAdd(ChatClientAgent item)
    {
        lock (_gate)
        {
            if (_agents.TryGetValue(item.Id, out var existing))
            {
                return existing;
            }

            _agents[item.Id] = item;
            return item;
        }
    }

    /// <summary>
    /// Attempts to retrieve a cached agent by identifier.
    /// </summary>
    /// <param name="agentId">The agent identifier to find.</param>
    /// <param name="agent">The cached agent when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the agent exists in the cache.</returns>
    public bool TryGetAgentById(string agentId, out ChatClientAgent? agent)
    {
        lock (_gate)
        {
            return _agents.TryGetValue(agentId, out agent);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            _agents.Clear();
        }
    }

    /// <summary>
    /// Determines whether an agent identifier exists in the cache.
    /// </summary>
    /// <param name="agentId">The agent identifier to check.</param>
    /// <returns><see langword="true"/> when the identifier is cached.</returns>
    public bool Contains(string agentId)
    {
        lock (_gate)
        {
            return _agents.ContainsKey(agentId);
        }
    }

    /// <inheritdoc />
    public bool Contains(ChatClientAgent item)
    {
        lock (_gate)
        {
            return _agents.ContainsKey(item.Id);
        }
    }

    /// <inheritdoc />
    public void CopyTo(ChatClientAgent[] array, int arrayIndex)
    {
        lock (_gate)
        {
            _agents.Values.CopyTo(array, arrayIndex);
        }
    }

    /// <inheritdoc />
    public IEnumerator<ChatClientAgent> GetEnumerator()
    {
        lock (_gate)
        {
            return new List<ChatClientAgent>(_agents.Values).GetEnumerator();
        }
    }

    /// <inheritdoc />
    public bool Remove(ChatClientAgent item)
    {
        return Remove(item.Id);
    }

    /// <summary>
    /// Removes a cached agent by identifier.
    /// </summary>
    /// <param name="agentId">The agent identifier to remove.</param>
    /// <returns><see langword="true"/> when an agent was removed.</returns>
    public bool Remove(string agentId)
    {
        lock (_gate)
        {
            return _agents.Remove(agentId);
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
