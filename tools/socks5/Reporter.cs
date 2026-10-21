using System.Globalization;

namespace WinForward.Socks5Tool;

/// <summary>
/// 同时写到标准输出与（可选）报告文件。每行立即落盘、立即关闭句柄，
/// 外部脚本可以边跑边轮询，不会被 sharing violation 挡住。
/// </summary>
internal sealed class Reporter
{
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _path;
    private readonly object _gate = new();

    internal Reporter(string? path) => _path = path;

    internal void WriteLine(string line)
    {
        lock (_gate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
            if (_path is null)
            {
                return;
            }

            using FileStream stream = new(_path, FileMode.Append, FileAccess.Write,
                                           FileShare.ReadWrite | FileShare.Delete);
            using StreamWriter writer = new(stream, Utf8NoBom);
            writer.WriteLine(line);
        }
    }

    internal void WriteLine(FormattableString line) =>
        WriteLine(line.ToString(CultureInfo.InvariantCulture));
}
