using Microsoft.Extensions.Logging;

namespace DiscordScraper.TestSupport.Observers;

/// <summary>
/// An <see cref="ILoggerProvider"/> that captures all log entries into an in-memory list.
/// Register via <c>services.AddLogging(b => b.AddProvider(new CapturingLoggerProvider()))</c>
/// and inspect <see cref="Entries"/> after the run.
///
/// Thread-safe: entries are captured under a lock; the list snapshot from <see cref="Entries"/>
/// is a point-in-time copy.
///
/// Primary use: detecting Mongo E11000 duplicate-key exceptions that surface as logged errors
/// from the MassTransit Mongo saga repository during concurrent timeout-expired retry storms.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<CapturedLogEntry> _entries = [];
    private readonly object _lock = new();

    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (_lock)
                return _entries.ToList();
        }
    }

    /// <summary>
    /// Returns all captured entries at or above <paramref name="level"/> whose formatted
    /// message or exception message contains <paramref name="fragment"/> (case-insensitive).
    /// </summary>
    public IReadOnlyList<CapturedLogEntry> Find(LogLevel level, string fragment)
        => Entries
            .Where(e => e.Level >= level
                && (e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                    || (e.Exception?.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();

    /// <summary>Shorthand: entries that mention E11000 or DuplicateKey at Error level.</summary>
    public IReadOnlyList<CapturedLogEntry> DuplicateKeyErrors =>
        Find(LogLevel.Error, "E11000")
            .Concat(Find(LogLevel.Error, "DuplicateKey"))
            .Concat(Find(LogLevel.Error, "duplicate key"))
            .Distinct()
            .ToList();

    internal void Add(CapturedLogEntry entry)
    {
        lock (_lock)
            _entries.Add(entry);
    }

    public ILogger CreateLogger(string categoryName)
        => new CapturingLogger(categoryName, this);

    public void Dispose() { }
}

/// <summary>A single captured log entry.</summary>
public sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception);

internal sealed class CapturingLogger(string category, CapturingLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        provider.Add(new CapturedLogEntry(category, logLevel, eventId, message, exception));
    }
}

internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();
    public void Dispose() { }
}
