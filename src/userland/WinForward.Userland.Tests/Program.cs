using WinForward.Userland.Tests;

if (args.Length == 0 || args[0] == "test")
{
    RegisterAll();
    return TestRunner.RunAll();
}

if (args[0] == "abi")
{
    DeviceAbiTests.Register();
    WinForward.Userland.Tests.Abi.AbiConsistencyTests.Register();
    return TestRunner.RunAll();
}

if (args[0] == "emit-fixtures")
{
    string directory = DifferentialFixtureWriter.ResolveDirectory(args.Length > 1 ? args[1] : null);
    int count = DifferentialFixtureWriter.Write(directory, DifferentialCases.All);
    Console.WriteLine($"[fixtures] {count} 个用例写入 {directory}");
    return 0;
}

if (args[0] == "vm-check")
{
    return await VmChecks.RunAsync(args[1..]);
}

Console.Error.WriteLine($"未知子命令：{args[0]}");
Console.Error.WriteLine("用法：WinForward.Userland.Tests [test | abi | emit-fixtures [目录] | vm-check]");
return 2;

static void RegisterAll()
{
    ForwardedLayoutTests.Register();
    ForwardedCompilerTests.Register();
    LayoutTests.Register();
    CompilerTests.Register();
    InvalidInputTests.Register();
    ProcessPathTests.Register();
    DifferentialTests.Register();
    ConfigurationTests.Register();
    RuleOrderTests.Register();
    DeviceAbiTests.Register();
    Socks5Tests.Register();
    Socks5ConfigurationTests.Register();
    RelayPumpTests.Register();
    WinForward.Userland.Tests.Abi.AbiConsistencyTests.Register();
}
