using WinForward.Userland.Tests;

if (args.Length == 0 || args[0] == "test")
{
    LayoutTests.Register();
    CompilerTests.Register();
    InvalidInputTests.Register();
    ProcessPathTests.Register();
    DifferentialTests.Register();
    return TestRunner.RunAll();
}

if (args[0] == "emit-fixtures")
{
    string directory = DifferentialFixtureWriter.ResolveDirectory(args.Length > 1 ? args[1] : null);
    int count = DifferentialFixtureWriter.Write(directory, DifferentialCases.All);
    Console.WriteLine($"[fixtures] {count} 个用例写入 {directory}");
    return 0;
}

Console.Error.WriteLine($"未知子命令：{args[0]}");
Console.Error.WriteLine("用法：WinForward.Userland.Tests [test | emit-fixtures [目录]]");
return 2;
