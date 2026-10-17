using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Oscova.Agents.Channels;

/// <summary>Defers attachment download until the host accepts its metadata and disposes the returned stream.</summary>
public sealed record ChannelAttachment(
    string FileName,
    string? ContentType,
    long Size,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);
