using System.Collections.Concurrent;

namespace Pipeline.Monitor;

/// <summary>
/// Thread-safe shared log that background services write to and the watch command tails.
/// </summary>
public sealed class MonitorLog
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>
    /// All entries in the log, in order.
    /// </summary>
    public IReadOnlyCollection<LogEntry> Entries => _entries;

    /// <summary>
    /// Fired when a new entry is appended.
    /// </summary>
    public event Action<LogEntry>? EntryAdded;

    public void Info(string source, string message)
        => Append(LogLevel.Info, source, message);

    public void Warn(string source, string message)
        => Append(LogLevel.Warn, source, message);

    public void Error(string source, string message)
        => Append(LogLevel.Error, source, message);

    public void Tool(string source, string message)
        => Append(LogLevel.Tool, source, message);

    private void Append(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, source, message);
        _entries.Enqueue(entry);
        EntryAdded?.Invoke(entry);
    }
}

public enum LogLevel
{
    Info,
    Warn,
    Error,
    Tool,
}

/// <summary>
/// A timestamped log entry from a background service.
/// </summary>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Source, string Message);
