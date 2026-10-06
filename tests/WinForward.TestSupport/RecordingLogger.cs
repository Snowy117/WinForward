using Microsoft.Extensions.Logging;

namespace WinForward.TestSupport;

internal sealed record RecordedEvent(
    LogLevel Level,
    EventId EventId,
    string Category,
    IReadOnlyList<KeyValuePair<string, object?>> Fields,
    string Message,
    Exception? Exception)
{
    public string Name => EventId.Name ?? string.Empty;

    public void Deconstruct(out LogLevel level, out IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        level = Level;
        fields = Fields;
    }

    public void Deconstruct(out LogLevel level, out string name, out IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        level = Level;
        name = Name;
        fields = Fields;
    }

    public object? Field(string key)
    {
        foreach (var field in Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal)) return field.Value;
        }

        return null;
    }
}

internal sealed class RecordingLogger(Func<LogLevel, bool>? isEnabled = null, string category = "test") : ILogger
{
    private readonly Lock _gate = new();
    private readonly List<RecordedEvent> _events = [];
    private readonly List<(LogLevel Level, string Message)> _lines = [];

    public IReadOnlyList<RecordedEvent> Events
    {
        get
        {
            lock (_gate) return [.. _events];
        }
    }

    public IReadOnlyList<(LogLevel Level, string Message)> Lines
    {
        get
        {
            lock (_gate) return [.. _lines];
        }
    }

    public int WarnCount => Lines.Count(line => line.Level == LogLevel.Warning);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && (isEnabled?.Invoke(logLevel) ?? true);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var fields = SelectFields(state);
        lock (_gate)
        {
            _lines.Add((logLevel, message));
            _events.Add(new RecordedEvent(logLevel, eventId, category, fields, message, exception));
        }
    }

    private static List<KeyValuePair<string, object?>> SelectFields<TState>(TState state)
    {
        var selected = new List<KeyValuePair<string, object?>>();
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> pairs) return selected;
        foreach (var pair in pairs)
        {
            if (string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal)) continue;
            selected.Add(pair);
        }

        return selected;
    }
}

internal sealed class RecordingLoggerProvider(RecordingLogger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => logger;

    public void Dispose()
    {
    }
}
