using WinForward.Configuration;
using WinForward.Runtime;

namespace WinForward.Core.Tests;

/// <summary>
/// Captures every structured event and plain-text line with its level; <see cref="WarnCount"/>
/// derives from the recorded lines so sink tests can assert the rate-limited warning fired.
/// Recording is lock-guarded and <see cref="Lines"/>/<see cref="Events"/> return snapshots, so a
/// test may enumerate while a background capture/proxy thread is still logging. The optional
/// <c>isEnabled</c> predicate lets a test model a threshold (the production default disables
/// debug), while recording itself stays unconditional.
/// </summary>
internal sealed class RecordingRuntimeLogger(Func<RuntimeLogLevel, bool>? isEnabled = null) : IRuntimeLogger
{
    private readonly Lock _gate = new();
    private readonly List<(RuntimeLogLevel Level, string Name, RuntimeLogField[] Fields)> _events = [];
    private readonly List<(RuntimeLogLevel Level, string Message)> _lines = [];

    public IReadOnlyList<(RuntimeLogLevel Level, string Name, RuntimeLogField[] Fields)> Events
    {
        get
        {
            lock (_gate) return [.. _events];
        }
    }

    public IReadOnlyList<(RuntimeLogLevel Level, string Message)> Lines
    {
        get
        {
            lock (_gate) return [.. _lines];
        }
    }

    public int WarnCount => Lines.Count(line => line.Level == RuntimeLogLevel.Warn);

    public bool IsEnabled(RuntimeLogLevel level) => isEnabled?.Invoke(level) ?? true;

    public void Trace(string message) => Add(RuntimeLogLevel.Trace, message);

    public void Debug(string message) => Add(RuntimeLogLevel.Debug, message);

    public void Info(string message) => Add(RuntimeLogLevel.Info, message);

    public void Warn(string message) => Add(RuntimeLogLevel.Warn, message);

    public void Error(string message) => Add(RuntimeLogLevel.Error, message);

    public void Event(RuntimeLogLevel level, string eventName, params RuntimeLogField[] fields)
    {
        lock (_gate) _events.Add((level, eventName, fields));
    }

    private void Add(RuntimeLogLevel level, string message)
    {
        lock (_gate) _lines.Add((level, message));
    }
}
