using System.Text.Encodings.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using WinForward.Configuration;

namespace WinForward.Runtime.Logging;

/// <summary>
/// The one composition point for runtime logging: the standard <c>Logging</c> section supplies the
/// levels, the formatter selection and every formatter option MEL binds; the post-configure actions
/// below supply this project's defaults, so they apply only where the operator was silent and never
/// depend on registration order.
/// </summary>
public static class RuntimeLogging
{
    /// <summary>The local wall-clock timestamp carrying its UTC offset.</summary>
    public const string TimestampFormat = "zzz yyyy-MM-dd HH:mm:ss.fff";

    public const string SimpleTimestampFormat = TimestampFormat + " ";

    /// <summary>The standard section MEL binds logging from.</summary>
    public const string LoggingSectionName = "Logging";

    public const string ConsoleSectionName = "Console";

    public const string LogLevelSectionName = "LogLevel";

    public const string FormatterNameKey = "FormatterName";

    /// <summary>
    /// Every level name MEL accepts, most severe first. The retired <c>logLevel</c> tokens
    /// (<c>info</c>, <c>warn</c>) are deliberately absent: the standard vocabulary is the migration.
    /// </summary>
    public static IReadOnlyList<string> AcceptedLogLevelNames { get; } =
    [
        nameof(LogLevel.Trace),
        nameof(LogLevel.Debug),
        nameof(LogLevel.Information),
        nameof(LogLevel.Warning),
        nameof(LogLevel.Error),
        nameof(LogLevel.Critical),
        nameof(LogLevel.None),
    ];

    /// <summary>The console formatters this project registers; any other name falls back in silence.</summary>
    public static IReadOnlyList<string> AcceptedFormatterNames { get; } =
    [
        ConsoleFormatterNames.Simple,
        ConsoleFormatterNames.Json,
        ConsoleFormatterNames.Systemd,
    ];

    /// <summary>
    /// Checks the two <c>Logging</c> values MEL leaves undiagnosed. A level name it cannot parse
    /// aborts logger creation with an <see cref="InvalidOperationException"/> instead of a
    /// diagnostic — and an operator migrating from <c>logLevel</c> will reach for <c>info</c> — while
    /// a formatter name it cannot resolve is dropped without a word. Both are reported here against
    /// the values that would work. An absent value is not an error: absence is what selects the
    /// defaults.
    /// </summary>
    public static bool TryValidate(IConfiguration configuration, out IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<ConfigDiagnostic>();
        var logLevel = configuration.GetSection(LoggingSectionName).GetSection(LogLevelSectionName);
        foreach (var category in logLevel.GetChildren())
        {
            if (category.Value is not null && !Enum.TryParse<LogLevel>(category.Value, ignoreCase: true, out _))
            {
                errors.Add(new ConfigDiagnostic(
                    $"{LoggingSectionName}.{LogLevelSectionName}.{category.Key}",
                    $"LogLevel value '{category.Value}' is not a level name; use {NameList(AcceptedLogLevelNames)}."));
            }
        }

        var console = configuration.GetSection(LoggingSectionName).GetSection(ConsoleSectionName);
        if (console[FormatterNameKey] is { } formatterName && !AcceptedFormatterNames.Contains(formatterName, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new ConfigDiagnostic(
                $"{LoggingSectionName}.{ConsoleSectionName}.{FormatterNameKey}",
                $"FormatterName value '{formatterName}' is not a formatter; use {NameList(AcceptedFormatterNames)}."));
        }

        diagnostics = errors;
        return errors.Count == 0;
    }

    /// <summary>
    /// Resolves the level a run applies to categories without their own entry: the operator's
    /// <c>Logging:LogLevel:Default</c>, or MEL's own default when the key is absent.
    /// </summary>
    public static LogLevel ResolveLogLevel(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Enum.TryParse<LogLevel>(
            configuration.GetSection(LoggingSectionName).GetSection(LogLevelSectionName)["Default"],
            ignoreCase: true,
            out var level)
            ? level
            : LogLevel.Information;
    }

    /// <summary>
    /// Resolves the formatter a run will use: the operator's <c>Logging:Console:FormatterName</c>
    /// when configured, otherwise the automatic rule — JSON when stderr is redirected (the service
    /// log) and the simple human format on an interactive terminal.
    /// </summary>
    public static string ResolveFormatterName(IConfiguration configuration, bool errorRedirected)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection(LoggingSectionName).GetSection(ConsoleSectionName)[FormatterNameKey]
            ?? (errorRedirected ? ConsoleFormatterNames.Json : ConsoleFormatterNames.Simple);
    }

    private static string NameList(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(names.Count - 1)) + " or " + names[^1];

    public static ILoggerFactory CreateLoggerFactory(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var logging = configuration.GetSection(LoggingSectionName);
        var console = logging.GetSection(ConsoleSectionName);
        return LoggerFactory.Create(builder =>
        {
            builder.AddConfiguration(logging);
            builder.AddConsole();
            builder.Services.PostConfigure<ConsoleLoggerOptions>(options =>
            {
                // Invariant, not a setting: a runtime record never shares stdout with the adapters
                // TSV or the validate confirmation.
                options.LogToStandardErrorThreshold = LogLevel.Trace;
                // An absent FormatterName means the automatic rule; the options default ("simple")
                // would silently disable it.
                if (console[FormatterNameKey] is null)
                {
                    options.FormatterName = Console.IsErrorRedirected ? ConsoleFormatterNames.Json : ConsoleFormatterNames.Simple;
                }
            });
            builder.Services.PostConfigure<SimpleConsoleFormatterOptions>(options => options.TimestampFormat ??= SimpleTimestampFormat);
            builder.Services.PostConfigure<JsonConsoleFormatterOptions>(options =>
            {
                options.TimestampFormat ??= TimestampFormat;
                var writer = options.JsonWriterOptions;
                if (writer.Encoder is null)
                {
                    // MEL's default encoder escapes every non-ASCII character; runtime log lines are
                    // read by operators and by log processors that accept UTF-8.
                    writer.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
                    options.JsonWriterOptions = writer;
                }
            });
        });
    }
}
