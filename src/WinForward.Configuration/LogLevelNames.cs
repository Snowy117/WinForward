using Microsoft.Extensions.Logging;

namespace WinForward.Configuration;

public static class LogLevelNames
{
    private const string Error = "error";
    private const string Warn = "warn";
    private const string Info = "info";
    private const string Debug = "debug";
    private const string Trace = "trace";

    public static bool TryParse(string? value, out LogLevel level)
    {
        level = LogLevel.Information;
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case Error:
                level = LogLevel.Error;
                return true;
            case Warn:
                level = LogLevel.Warning;
                return true;
            case Info:
                level = LogLevel.Information;
                return true;
            case Debug:
                level = LogLevel.Debug;
                return true;
            case Trace:
                level = LogLevel.Trace;
                return true;
            default:
                return false;
        }
    }

    public static string ToConfigToken(LogLevel level) => level switch
    {
        LogLevel.Error => Error,
        LogLevel.Warning => Warn,
        LogLevel.Information => Info,
        LogLevel.Debug => Debug,
        LogLevel.Trace => Trace,
        _ => level.ToString().ToLowerInvariant(),
    };
}
