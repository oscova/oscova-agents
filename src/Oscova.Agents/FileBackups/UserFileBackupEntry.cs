using System;

namespace Oscova.Agents.FileBackups;

/// <summary>
/// Describes one captured version of a user file.
/// </summary>
public sealed record UserFileBackupEntry(
    string Id,
    string RelativePath,
    DateTimeOffset CreatedUtc,
    long SizeBytes);
