using WinForward.Controller;

if (args.Length == 0)
{
    return PrintUsage();
}

try
{
    return args[0] switch
    {
        "run" => await RunCommand.ExecuteAsync(args[1..]),
        "check" => CheckCommand.Execute(args[1..]),
        "state" => StateCommand.Execute(args[1..]),
        "disable" => DisableCommand.Execute(args[1..]),
        "echo" => await EchoCommand.ExecuteAsync(args[1..]),
        "client" => ClientCommand.Execute(args[1..]),
        _ => PrintUsage(),
    };
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"参数错误：{exception.Message}");
    return 2;
}

static int PrintUsage()
{
    Console.Error.WriteLine("""
        用法：
          WinForward.Controller check --config <配置.json>
          WinForward.Controller run   --config <配置.json> [--duration-ms N] [--stop-file <文件>] [--no-redirect-records] [--report <文件>]
          WinForward.Controller state [--report <文件>]
          WinForward.Controller disable
          WinForward.Controller echo  [--port N] [--connections N] [--report <文件>] [--timeout-ms N]
          WinForward.Controller client --target <host:port> [--bytes N] [--seed N] [--family 4|6] [--timeout-ms N]

        run 需要管理员权限（设备 DACL 只允许 SYSTEM 与 Administrators）。
        """);
    return 2;
}
