using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;

namespace Oscova.Agents.Infrastructure.Chat;

/// <summary>Fails an event stream whose event reports a provider error inside a successful response, as an error status would.</summary>
internal sealed class StreamedErrorPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        CheckEventStream(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        CheckEventStream(message);
    }

    private static void CheckEventStream(PipelineMessage message)
    {
        if (message.Response is { ContentStream: { } content } response
            && response.Headers.TryGetValue("Content-Type", out var contentType)
            && contentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.ContentStream = new ErrorCheckingStream(content);
        }
    }

    /// <summary>Replays the stream one event at a time, checking each before the SDK parses it.</summary>
    private sealed class ErrorCheckingStream(Stream inner) : Stream
    {
        private readonly StreamReader _reader = new(inner, Encoding.UTF8);
        private byte[] _pending = [];
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_offset == _pending.Length)
            {
                var builder = new StringBuilder();
                while (_reader.ReadLine() is { } line && Append(builder, line)) { }

                if (!Load(builder)) { return 0; }
            }

            return Copy(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset == _pending.Length)
            {
                var builder = new StringBuilder();
                while (await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line && Append(builder, line)) { }

                if (!Load(builder)) { return 0; }
            }

            return Copy(buffer.Span);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _reader.Dispose(); }

            base.Dispose(disposing);
        }

        // Adds one line to the current event and reports whether the event continues.
        private static bool Append(StringBuilder builder, string line)
        {
            builder.Append(line).Append('\n');
            return line.Length > 0;
        }

        private bool Load(StringBuilder builder)
        {
            if (builder.Length == 0) { return false; }

            var text = builder.ToString();
            // Like the SDK's parser, only an event completed by a blank line counts; one cut off at the end is discarded.
            if (text == "\n" || text.EndsWith("\n\n", StringComparison.Ordinal))
            {
                ThrowIfError(text);
            }

            _pending = Encoding.UTF8.GetBytes(text);
            _offset = 0;
            return true;
        }

        private int Copy(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        private static void ThrowIfError(string eventText)
        {
            // The SSE parser joins an event's data lines with line feeds, dropping one space after the colon.
            var data = string.Join('\n', eventText.Split('\n')
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]));
            if (!data.TrimStart().StartsWith('{')) { return; }

            string? providerMessage;
            try
            {
                using var document = JsonDocument.Parse(data);
                if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null) { return; }

                providerMessage = error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }
            catch (JsonException)
            {
                return;
            }

            throw new ClientResultException(string.IsNullOrWhiteSpace(providerMessage)
                ? "The model provider reported an error in its response stream."
                : providerMessage);
        }
    }
}
