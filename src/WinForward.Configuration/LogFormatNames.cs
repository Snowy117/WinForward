
namespace WinForward.Configuration;

public static class LogFormatNames
{
    private const string Auto = "auto";
    private const string Simple = "simple";
    private const string Json = "json";

    public static bool TryParse(string? value, out LogFormat format)
    {
        format = LogFormat.Auto;
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case Auto:
                format = LogFormat.Auto;
                return true;
            case Simple:
                format = LogFormat.Simple;
                return true;
            case Json:
                format = LogFormat.Json;
                return true;
            default:
                return false;
        }
    }

    public static string ToConfigToken(LogFormat format) => format switch
    {
        LogFormat.Simple => Simple,
        LogFormat.Json => Json,
        _ => Auto,
    };
}
