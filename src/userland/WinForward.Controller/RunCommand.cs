using WinForward.Userland.Configuration;
using WinForward.Userland.Forwarded;
using WinForward.Userland.Control;
using WinForward.Userland.Policy;

namespace WinForward.Controller;

internal static class RunCommand
{
    private const int PollMilliseconds = 100;

    internal static async Task<int> ExecuteAsync(string[] args)
    {
        CommandLine line = CommandLine.Parse(args);
        string configPath = line.Require("config");
        int durationMs = line.GetInt("duration-ms", 0);
        string? stopFile = line.Get("stop-file");
        string? reportPath = line.Get("report");
        bool attachRedirectRecords = !line.Has("no-redirect-records");

        ControllerConfiguration configuration;
        byte[] blob;
        byte[]? forwardedBlob;
        try
        {
            // 适配器选择器（stable id / 接口名 → ifIndex）只在 Windows 上解析；
            // Linux 上保持没有 resolver，带 adapter 的配置按 B2 的规则在配置期报错。
            IAdapterIdResolver? adapterResolver =
                OperatingSystem.IsWindows() ? new WindowsAdapterIdResolver() : null;
            configuration = PolicyConfigurationDocument.LoadConfiguration(configPath, adapterResolver);
            blob = RuleBlobCompiler.Compile(configuration.Policy, FwpmAppIdNormalizer.ForWindows());
            forwardedBlob = configuration.Forwarded is null
                                ? null
                                : ForwardedBlobCompiler.Compile(configuration.Forwarded);
            // B4a 的驱动不执行 redirect：在这里（打开设备、绑监听之前）就拒绝，
            // 而不是等 relay 起好之后由 ioctl 返回 Win32 50 留下半启动状态。
            ForwardedRuntimeSupport.EnsureExecutable(configuration.Forwarded);
        }
        catch (Exception exception) when (exception is ConfigurationException or PolicyCompileException)
        {
            Console.Error.WriteLine($"配置无法编译：{exception.GetType().Name}: {exception.Message}");
            return 2;
        }

        using ReportWriter report = new(reportPath);
        PolicyTable policy = configuration.Policy;
        report.WriteLine($"policy rules={policy.Rules.Count} fallback={policy.FallbackAction} blob={blob.Length} " +
                         $"config={configPath} records={attachRedirectRecords} " +
                         $"socks5={configuration.Socks5?.ToString() ?? "none"}");

        using CancellationTokenSource shutdown = new();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        WfpController? controller = null;
        try
        {
            controller = WfpController.Start(new ControllerSettings
            {
                Policy = policy,
                RuleBlob = blob,
                Report = report,
                AttachRedirectRecords = attachRedirectRecords,
                Socks5 = configuration.Socks5,
                Forwarded = configuration.Forwarded,
                ForwardedBlob = forwardedBlob,
                Upstreams = configuration.Upstreams,
                ForwardedUpstreams = configuration.ForwardedUpstreams,
            });

            await WaitForShutdownAsync(shutdown.Token, durationMs, stopFile, report);
        }
        catch (OperationCanceledException)
        {
            report.WriteLine("controller-run cancelled=true");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException
                                               or System.Net.Sockets.SocketException)
        {
            report.WriteLine($"controller-error {exception.GetType().Name}: {exception.Message}");
            if (controller is not null)
            {
                await controller.DisposeAsync();
            }

            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        await controller!.DisposeAsync();
        report.WriteLine("controller-exit code=0");
        return 0;
    }

    /// <summary>
    /// 等到「时长到期」「停止文件出现」「Ctrl+C」三者之一。停止文件是给脚本用的控制通道：
    /// 它比杀进程更能验证「控制器是主动退出的」，退出时仍然走完整的停监听 → 关重定向顺序。
    /// </summary>
    private static async Task WaitForShutdownAsync(CancellationToken token,
                                                   int durationMs,
                                                   string? stopFile,
                                                   TextWriter report)
    {
        report.WriteLine($"controller-run duration_ms={durationMs} stop_file={stopFile ?? "none"}");
        using CancellationTokenSource? timer = durationMs > 0 ? new CancellationTokenSource(durationMs) : null;
        using CancellationTokenSource linked = timer is null
                                                  ? CancellationTokenSource.CreateLinkedTokenSource(token)
                                                  : CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);

        if (stopFile is null)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            return;
        }

        while (!File.Exists(stopFile))
        {
            await Task.Delay(PollMilliseconds, linked.Token);
        }
    }
}
