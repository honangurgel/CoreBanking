using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CoreBanking.IntegrationTests;

internal sealed class RecordingLogProvider : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);
    public void Dispose() { }

    internal sealed record LogEntry(string Category, IReadOnlyDictionary<string, object?> Properties);

    private sealed class RecordingLogger(RecordingLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (category is not ("Program" or "CoreBanking.Worker.PixWorker" or "CoreBanking.Worker.Worker"
                or "CoreBanking.Simulator.Settlements"))
                return;
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                provider.Entries.Enqueue(new(category, properties.ToDictionary(item => item.Key, item => item.Value)));
        }
    }
}
