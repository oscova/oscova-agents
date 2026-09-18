using System;
using System.Collections.Generic;
using System.IO;

namespace Oscova.Agents.Workspace;

/// <summary>Performs physical workspace operations under the current directory grants.</summary>
public interface IWorkspaceFileAccess
{
    /// <summary>Opens a readable, seekable file owned by the caller.</summary>
    Stream OpenRead(string path);

    /// <summary>Writes through a contained stream, atomically replacing the file unless appending.</summary>
    void Write(string path, Action<Stream> write, bool append = false);

    /// <summary>Deletes a contained file when it exists.</summary>
    void Delete(string path);

    /// <summary>Returns contained entry metadata, or <see langword="null"/> when the entry is missing.</summary>
    WorkspaceFileEntry? GetEntry(string path);

    /// <summary>Enumerates direct children without following links.</summary>
    IEnumerable<WorkspaceFileEntry> Enumerate(string directory);
}
