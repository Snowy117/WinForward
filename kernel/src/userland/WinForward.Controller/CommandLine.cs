namespace WinForward.Controller;

internal sealed class CommandLine
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private CommandLine()
    {
    }

    internal static CommandLine Parse(string[] args)
    {
        CommandLine line = new();
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"无法识别的参数「{argument}」（选项都以 -- 开头）");
            }

            string name = argument[2..];
            string value = "1";
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }

            line._values[name] = value;
        }

        return line;
    }

    internal string? Get(string name) => _values.TryGetValue(name, out string? value) ? value : null;

    internal string Require(string name) =>
        Get(name) ?? throw new ArgumentException($"缺少必填选项 --{name}");

    internal int GetInt(string name, int fallback)
    {
        string? text = Get(name);
        if (text is null)
        {
            return fallback;
        }

        return int.TryParse(text, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out int value)
                   ? value
                   : throw new ArgumentException($"选项 --{name} 不是整数：「{text}」");
    }

    internal long GetLong(string name, long fallback)
    {
        string? text = Get(name);
        if (text is null)
        {
            return fallback;
        }

        return long.TryParse(text, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out long value)
                   ? value
                   : throw new ArgumentException($"选项 --{name} 不是整数：「{text}」");
    }

    internal bool Has(string name) => _values.ContainsKey(name);
}
