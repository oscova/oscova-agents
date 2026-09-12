using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Oscova.Agents.Tests.Framework.Logging
{
    public sealed class TestLogEntry
    {
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
        public string Category { get; init; } = string.Empty;
        public LogLevel Level { get; init; }
        public EventId EventId { get; init; }
        public string Message { get; init; } = string.Empty;
        public Exception? Exception { get; init; }
    }

    public sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<TestLogEntry> _entries = new();

        public IEnumerable<TestLogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName, _entries);

        public void Dispose() { }
    }

    internal sealed class TestLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly ConcurrentQueue<TestLogEntry> _entries;

        public TestLogger(string categoryName, ConcurrentQueue<TestLogEntry> entries)
        {
            _categoryName = categoryName;
            _entries = entries;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true; // filtering is handled by LoggerFactory

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter != null ? formatter(state, exception) : state?.ToString() ?? string.Empty;
            _entries.Enqueue(new TestLogEntry
            {
                Category = _categoryName,
                Level = logLevel,
                EventId = eventId,
                Exception = exception,
                Message = message ?? string.Empty,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
