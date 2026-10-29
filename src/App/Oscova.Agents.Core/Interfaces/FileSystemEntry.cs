namespace Oscova.Agents.Core.Interfaces;

public sealed record FileSystemEntry(string Name, bool IsDirectory, long Size, DateTime LastModified);
