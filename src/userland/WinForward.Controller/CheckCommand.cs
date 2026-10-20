using WinForward.Userland.Configuration;
using WinForward.Userland.Policy;

namespace WinForward.Controller;

internal static class CheckCommand
{
    internal static int Execute(string[] args)
    {
        CommandLine line = CommandLine.Parse(args);
        string path = line.Require("config");

        try
        {
            PolicyTable policy = PolicyConfigurationDocument.Load(path);
            byte[] blob = RuleBlobCompiler.Compile(policy, FwpmAppIdNormalizer.ForWindows());
            Console.WriteLine($"policy rules={policy.Rules.Count} fallback={policy.FallbackAction} blob={blob.Length} bytes");
            Console.WriteLine($"check OK {path}");
            return 0;
        }
        catch (Exception exception) when (exception is ConfigurationException or PolicyCompileException)
        {
            Console.Error.WriteLine($"check FAIL {path}");
            Console.Error.WriteLine($"       {exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }
}
