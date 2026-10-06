using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinForward.Configuration;

namespace WinForward.Runtime.Logging;

public static class RuntimeLogging
{
    private const string TimestampFormat = "zzz yyyy-MM-dd HH:mm:ss.fff";
    private const string SimpleTimestampFormat = TimestampFormat + " ";

    private static readonly JsonWriterOptions s_relaxedJsonWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static LogFormat ResolveLogFormat(LogFormat format, bool errorRedirected)
    {
        if (format != LogFormat.Auto) return format;
        return errorRedirected ? LogFormat.Json : LogFormat.Simple;
    }

    public static ILoggerFactory CreateLoggerFactory(LogLevel level, LogFormat format)
    {
        var resolved = ResolveLogFormat(format, Console.IsErrorRedirected);
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(level);
            builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            if (resolved == LogFormat.Json)
            {
                builder.AddJsonConsole(options =>
                {
                    options.TimestampFormat = TimestampFormat;
                    options.JsonWriterOptions = s_relaxedJsonWriterOptions;
                });
            }
            else
            {
                builder.AddSimpleConsole(options => options.TimestampFormat = SimpleTimestampFormat);
            }
        });
    }

}
