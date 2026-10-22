using WinForward.Userland.Configuration;
using WinForward.Userland.Control;
using WinForward.Userland.Forwarded;
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
            IAdapterIdResolver? adapterResolver =
                OperatingSystem.IsWindows() ? new WindowsAdapterIdResolver() : null;
            ControllerConfiguration configuration =
                PolicyConfigurationDocument.LoadConfiguration(path, adapterResolver);
            PolicyTable policy = configuration.Policy;
            // 没有 process 规则时编译器不会调用 normalizer；用无副作用实现，
            // 非 Windows 也能离线跑 check（有 process 规则时仍需要 Windows 解析器）。
            IProcessPathNormalizer normalizer = policy.Rules.Any(rule => rule.Process is not null)
                                                    ? FwpmAppIdNormalizer.ForWindows()
                                                    : AsciiLowercaseNormalizer.Instance;
            byte[] blob = RuleBlobCompiler.Compile(policy, normalizer);
            Console.WriteLine($"policy rules={policy.Rules.Count} fallback={policy.FallbackAction} blob={blob.Length} bytes");

            if (configuration.Forwarded is not null)
            {
                ForwardedRuntimeSupport.EnsureExecutable(configuration.Forwarded);
                byte[] forwardedBlob = ForwardedBlobCompiler.Compile(configuration.Forwarded);
                Console.WriteLine(
                    $"forwarded rules={configuration.Forwarded.Rules.Count} " +
                    $"fallback={configuration.Forwarded.FallbackAction} " +
                    $"upstreams={configuration.ForwardedUpstreams.Count} blob={forwardedBlob.Length} bytes");
            }

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
