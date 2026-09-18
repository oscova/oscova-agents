using System;
using System.IO;

namespace Oscova.Agents.Workspace;

/// <summary>Describes one workspace entry without requiring a pathname-based metadata query.</summary>
public sealed record WorkspaceFileEntry(
    string Path,
    FileAttributes Attributes,
    long Length,
    DateTime LastWriteTimeUtc);
