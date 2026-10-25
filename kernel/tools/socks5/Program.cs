using System.Globalization;
using System.Net;
using WinForward.Socks5Tool;

if (args.Length == 0)
{
    return Usage();
}

string command = args[0];
Dictionary<string, string> options = ParseOptions(args[1..], out string? parseError);
if (parseError is not null)
{
    Console.Error.WriteLine(parseError);
    return 2;
}

int port = Integer(options, "port", 0);
int connections = Integer(options, "connections", 0);
int timeoutMs = Integer(options, "timeout-ms", 120000);
IPAddress listen = ListenAddress(options);
Reporter report = new(options.GetValueOrDefault("report"));

return command switch
{
    "serve" => await Socks5Upstream.RunAsync(listen, port, connections, timeoutMs, report),
    "echo" => await EchoTarget.RunAsync(listen, port, connections, timeoutMs, report),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        最小 SOCKS5 服务端与回显目标，只给端到端测试当替身用。

          wf-socks5 serve --listen <地址> --port <端口> [--connections N] [--timeout-ms N] [--report <文件>]
          wf-socks5 echo  --listen <地址> --port <端口> [--connections N] [--timeout-ms N] [--report <文件>]

        serve：no-auth CONNECT，支持 IPv4 / IPv6 / 域名的目标；结束时打印 connections= / connects= / bytes=。
        echo ：裸 TCP 回显目标，充当跨机 target。
        --connections 0（默认）表示一直跑到超时或 Ctrl+C。
        """);
    return 2;
}

static Dictionary<string, string> ParseOptions(string[] arguments, out string? error)
{
    Dictionary<string, string> options = new(StringComparer.Ordinal);
    error = null;
    for (int index = 0; index < arguments.Length; index++)
    {
        string argument = arguments[index];
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            error = $"无法识别的参数「{argument}」（选项都以 -- 开头）";
            return options;
        }

        string name = argument[2..];
        if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = $"选项 --{name} 缺少值";
            return options;
        }

        options[name] = arguments[++index];
    }

    return options;
}

static string? Value(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out string? value) ? value : null;

static int Integer(Dictionary<string, string> options, string name, int fallback)
{
    string? text = Value(options, name);
    if (text is null)
    {
        return fallback;
    }

    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
               ? value
               : throw new ArgumentException($"选项 --{name} 不是整数：「{text}」");
}

static IPAddress ListenAddress(Dictionary<string, string> options)
{
    string text = Value(options, "listen") ?? "127.0.0.1";
    return IPAddress.TryParse(text, out IPAddress? address)
               ? address
               : throw new ArgumentException($"选项 --listen 不是 IP 字面量：「{text}」");
}
