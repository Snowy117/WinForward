using WinForward.Userland.Device;

namespace WinForward.Controller;

/// <summary>把驱动置于「只放行、不重定向」：关闭 enabled，保留规则表。用于测试清理与现场排查。</summary>
internal static class DisableCommand
{
    internal static int Execute(string[] args)
    {
        _ = args;
        try
        {
            using WfpDeviceClient device = WfpDeviceClient.Open();
            device.SetConfig(ProbeConfig.Disabled());
            ProbeConfig config = device.GetConfig();
            Console.WriteLine($"disabled enabled={config.Enabled} listener_port={config.ListenerPort}");
            return config.Enabled == 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"关闭重定向失败：{exception.Message}");
            return 1;
        }
    }
}
