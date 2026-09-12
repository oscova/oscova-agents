namespace Oscova.Agents.Avatar.Contracts;

/// <summary>Controls whether an engine run accepts one or multiple active clients.</summary>
public enum AvatarAccessMode
{
    /// <summary>Allows other paired clients to activate during the same engine run.</summary>
    Shared,

    /// <summary>Rejects other clients until the engine restarts.</summary>
    Exclusive
}
