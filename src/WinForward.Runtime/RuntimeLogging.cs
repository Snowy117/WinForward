using System.Globalization;
using System.Net;
using System.Text;
using WinForward.Configuration;
using WinForward.Core;

namespace WinForward.Runtime;

public readonly record struct RuntimeLogField(string Key, object? Value);

public interface IRuntimeLogger
{
    bool IsEnabled(RuntimeLogLevel level) => level <= RuntimeLogLevel.Info;
    void Trace(string message) { }
    void Debug(string message) { }
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Event(RuntimeLogLevel level, string eventName, params RuntimeLogField[] fields) { }
}

public sealed class NullRuntimeLogger : IRuntimeLogger
{
    public static readonly NullRuntimeLogger Instance = new();

    private NullRuntimeLogger() { }

    public bool IsEnabled(RuntimeLogLevel level) => false;
    public void Trace(string message) { }
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Event(RuntimeLogLevel level, string eventName, params RuntimeLogField[] fields) { }
}

public sealed class ConsoleRuntimeLogger : IRuntimeLogger
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly RuntimeLogLevel _threshold;
    private readonly TextWriter _writer;
    private readonly Lock _gate = new();

    public ConsoleRuntimeLogger(RuntimeLogLevel threshold = RuntimeLogLevel.Info, TextWriter? writer = null)
    {
        _threshold = threshold;
        _writer = writer ?? Console.Error;
    }

    public bool IsEnabled(RuntimeLogLevel level) => level <= _threshold;

    public void Trace(string message) => Write(RuntimeLogLevel.Trace, message);
    public void Debug(string message) => Write(RuntimeLogLevel.Debug, message);
    public void Info(string message) => Write(RuntimeLogLevel.Info, message);
    public void Warn(string message) => Write(RuntimeLogLevel.Warn, message);
    public void Error(string message) => Write(RuntimeLogLevel.Error, message);

    public void Event(RuntimeLogLevel level, string eventName, params RuntimeLogField[] fields)
    {
        if (!IsEnabled(level)) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        try
        {
            var builder = new StringBuilder(eventName.Length + 64);
            builder.Append(eventName);
            foreach (var field in fields)
            {
                if (field.Value is null) continue;
                builder.Append(' ').Append(field.Key).Append('=').Append(FormatValue(field.Value));
            }
            Write(level, builder.ToString());
        }
        catch (Exception exception)
        {
            GC.KeepAlive(exception);
        }
    }

    private void Write(RuntimeLogLevel level, string message)
    {
        if (!IsEnabled(level)) return;
        try
        {
            var timestamp = DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var line = $"{timestamp} [{level.ToString().ToLowerInvariant()}] {SanitizeMessage(message)}";
            lock (_gate) _writer.WriteLine(line);
        }
        catch (Exception exception)
        {
            GC.KeepAlive(exception);
        }
    }

    private static string FormatValue(object value)
    {
        var text = value switch
        {
            Endpoint endpoint => FormatEndpoint(endpoint),
            IPEndPoint endpoint => endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{endpoint.Address}]:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}"
                : $"{endpoint.Address}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}",
            DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
            DateTime timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
            Enum enumValue => enumValue.ToString().ToLowerInvariant(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
        return NeedsQuoting(text) ? Quote(text) : text;
    }

    private static string FormatEndpoint(Endpoint endpoint) => endpoint.AddressFamily == AddressFamilyKind.IPv6
        ? $"[{endpoint.Address}]:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}"
        : $"{endpoint.Address}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}";

    private static string SanitizeMessage(string message) => message.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static bool NeedsQuoting(string value) => value.Length == 0 || value.Any(static character => char.IsWhiteSpace(character) || character is '"' or '=' || char.IsControl(character));

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            _ = character switch
            {
                '\\' => builder.Append("\\\\"),
                '"' => builder.Append("\\\""),
                '\r' => builder.Append("\\r"),
                '\n' => builder.Append("\\n"),
                '\t' => builder.Append("\\t"),
                _ when char.IsControl(character) => builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture)),
                _ => builder.Append(character),
            };
        }
        return builder.Append('"').ToString();
    }
}
