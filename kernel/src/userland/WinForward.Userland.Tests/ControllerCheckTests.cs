using WinForward.Controller;

namespace WinForward.Userland.Tests;

internal static class ControllerCheckTests
{
    internal static void Register()
    {
        TestRunner.Add("controller/纯文件名规则不要求 Windows 解析器", FileNameOnlyRuleWorksOffline);
        if (OperatingSystem.IsWindows())
        {
            TestRunner.Add("controller/路径型规则在 Windows 上因路径不存在可诊断地失败", PathRuleMissingFailsOnWindows);
        }
        else
        {
            TestRunner.Add("controller/路径型规则在非 Windows 上因平台不支持明确失败", PathRuleRequiresWindows);
        }
    }

    private static void FileNameOnlyRuleWorksOffline()
    {
        (int exit, _) = RunCheck("""
            { "fallbackAction": "permit", "rules": [ { "action": "redirect", "process": "svchost.exe" } ] }
            """);

        Check.Equal(0, exit, "filename 规则应能在没有 Windows 解析器的环境编译");
    }

    private static void PathRuleMissingFailsOnWindows()
    {
        (int exit, string error) = RunCheck("""
            { "fallbackAction": "permit", "rules": [ { "action": "permit", "process": "C:\\nope\\missing.exe" } ] }
            """);

        Check.Equal(2, exit, "路径不存在时必须给出可诊断的失败而不是崩溃");
        Check.True(error.Contains("PolicyCompileException", StringComparison.Ordinal),
                   $"失败原因应是路径解析失败；实际 stderr：{error}");
    }

    private static void PathRuleRequiresWindows()
    {
        (int exit, string error) = RunCheck("""
            { "fallbackAction": "permit", "rules": [ { "action": "permit", "process": "C:\\nope\\missing.exe" } ] }
            """);

        Check.Equal(2, exit, "路径型规则必须给出可诊断的失败而不是崩溃");
        Check.True(error.Contains("PlatformNotSupportedException", StringComparison.Ordinal),
                   $"失败原因应是平台不支持 Windows 解析器；实际 stderr：{error}");
    }

    private static (int Exit, string Error) RunCheck(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"wf-check-{Guid.NewGuid():N}.json");
        TextWriter originalError = Console.Error;
        using var capture = new StringWriter();
        try
        {
            File.WriteAllText(path, json);
            Console.SetError(capture);
            int exit = CheckCommand.Execute(new[] { "--config", path });
            return (exit, capture.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            File.Delete(path);
        }
    }
}
