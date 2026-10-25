using System.Text;

namespace WinForward.Controller;

/// <summary>
/// 同时写到标准输出与（可选的）报告文件。每一行都立即落盘，外部脚本可以边跑边读。
/// </summary>
/// <remarks>
/// 不用长期持有的 <see cref="StreamWriter"/>：报告文件会被测试脚本并发轮询，
/// 长期持有句柄会让读侧撞 sharing violation（Windows 实测）。逐行 append 并立刻关闭，
/// 读侧就永远不会被锁住。
/// </remarks>
internal sealed class ReportWriter : TextWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _path;
    private readonly object _gate = new();

    internal ReportWriter(string? path) => _path = path;

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(string? value)
    {
        lock (_gate)
        {
            Console.Out.Write(value);
            Append(value, newline: false);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            Console.Out.WriteLine(value);
            Console.Out.Flush();
            Append(value, newline: true);
        }
    }

    public override void Flush()
    {
        lock (_gate)
        {
            Console.Out.Flush();
        }
    }

    private void Append(string? value, bool newline)
    {
        if (_path is null || value is null)
        {
            return;
        }

        using FileStream stream = new(_path, FileMode.Append, FileAccess.Write,
                                       FileShare.ReadWrite | FileShare.Delete);
        using StreamWriter writer = new(stream, Utf8NoBom);
        if (newline)
        {
            writer.WriteLine(value);
        }
        else
        {
            writer.Write(value);
        }
    }
}
