using Microsoft.AspNetCore.Http.Features;

namespace Oscova.Agents.Host.Server.Hosting;

/// <summary>
/// Forces a flush after every write on the wrapped response body. The AG-UI hosting
/// package writes each SSE event via <c>SseFormatter.WriteAsync</c>, which calls
/// <c>Stream.WriteAsync</c> but never <c>FlushAsync</c>. Without a per-write flush,
/// Kestrel buffers the events and ships them in one burst when the response completes,
/// so the browser receives the whole run at once instead of streaming. Flushing here
/// makes each event leave the server as it is produced.
/// </summary>
internal sealed class AutoFlushStream(Stream inner) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        inner.Flush();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Wires per-event flushing for the AG-UI SSE endpoint so streamed events are not
/// buffered by Kestrel until the response ends.
/// </summary>
internal static class SseAutoFlushMiddleware
{
    public static IApplicationBuilder UseSseAutoFlush(this IApplicationBuilder app, PathString path)
    {
        return app.UseWhen(
            context => context.Request.Path.StartsWithSegments(path),
            branch => branch.Use(async (context, next) =>
            {
                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                var original = context.Response.Body;
                context.Response.Body = new AutoFlushStream(original);
                try
                {
                    await next().ConfigureAwait(false);
                }
                finally
                {
                    context.Response.Body = original;
                }
            }));
    }
}
