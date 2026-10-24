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
            // 只有路径型选择器需要 Windows 句柄法解析；纯文件名规则在 Linux 也能离线检查。
            // 编译器不会调用 normalizer 时，用明确拒绝调用的实现，避免静默返回错误路径。
            bool needsWindowsResolver = policy.Rules.Any(rule =>
                rule.Processes is { } processes &&
                processes.Any(selector => selector.Kind == ProcessSelectorKind.Path));
            IProcessPathNormalizer normalizer = needsWindowsResolver
                                                    ? WindowsProcessPathNormalizer.ForWindows()
                                                    : UnusedProcessPathNormalizer.Instance;
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
        catch (Exception exception) when (exception is ConfigurationException or PolicyCompileException
                                               or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"check FAIL {path}");
            Console.Error.WriteLine($"       {exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }

    private sealed class UnusedProcessPathNormalizer : IProcessPathNormalizer
    {
        internal static UnusedProcessPathNormalizer Instance { get; } = new();

        public ProcessPathResolution? Normalize(string path) =>
            throw new InvalidOperationException(
                $"规则没有进程选择器，编译器不应调用路径归一化：{path}");
    }
}
