using WinForward.Configuration;
using WinForward.Runtime;

namespace WinForward.Core.Tests;

/// <summary>
/// Captures every structured event and plain-text line with its level; <see cref="WarnCount"/>
/// derives from the recorded lines so sink tests can assert the rate-limited warning fired.
/// </summary>
internal sealed class RecordingRuntimeLogger : IRuntimeLogger
{
    public List<(RuntimeLogLevel Level, string Name, RuntimeLogField[] Fields)> Events { get; } = [];
    public List<(RuntimeLogLevel Level, string Message)> Lines { get; } = [];

    public int WarnCount => Lines.Count(line => line.Level == RuntimeLogLevel.Warn);

    public bool IsEnabled(RuntimeLogLevel level) => true;
    public void Trace(string message) => Lines.Add((RuntimeLogLevel.Trace, message));
    public void Debug(string message) => Lines.Add((RuntimeLogLevel.Debug, message));
    public void Info(string message) => Lines.Add((RuntimeLogLevel.Info, message));
    public void Warn(string message) => Lines.Add((RuntimeLogLevel.Warn, message));
    public void Error(string message) => Lines.Add((RuntimeLogLevel.Error, message));
    public void Event(RuntimeLogLevel level, string eventName, params RuntimeLogField[] fields) => Events.Add((level, eventName, fields));
}
