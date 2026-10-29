namespace Oscova.Agents.Core.ApiContracts.Nodes;

/// <summary>Carries a bounded file segment and the revision used to detect changes during a download.</summary>
public sealed record NodeFileChunk(long Length, long LastWriteTicks, byte[] Bytes);
