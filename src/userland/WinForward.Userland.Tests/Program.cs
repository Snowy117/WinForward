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
    WinForward.Userland.Tests.Abi.BlobAbiConsistencyTests.Register();
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
    AdapterResolverTests.Register();
    ForwardedCompilerTests.Register();
    ForwardedConfigurationTests.Register();
    LayoutTests.Register();
    CompilerTests.Register();
    CompilerSelectorTests.Register();
    CompilerLimitTests.Register();
    InvalidInputTests.Register();
    ProcessPathTests.Register();
    DifferentialTests.Register();
    ConfigurationTests.Register();
    ConfigurationArrayTests.Register();
    RuleOrderTests.Register();
    RuleOrderProcessTests.Register();
    ControllerCheckTests.Register();
    DeviceAbiTests.Register();
    Socks5Tests.Register();
    Socks5UdpCodecTests.Register();
    Socks5ConfigurationTests.Register();
    RelayPumpTests.Register();
    WinForward.Userland.Tests.Abi.AbiConsistencyTests.Register();
    WinForward.Userland.Tests.Abi.BlobAbiConsistencyTests.Register();
}
