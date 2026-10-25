using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>\\.\WfProbe</c> 的控制面客户端：下发配置与规则 blob，回读计数、规则信息与事件。
/// </summary>
/// <remarks>
/// 设备 DACL 只允许 SYSTEM 与 Administrators，IOCTL 入口还会再检查调用者令牌，
/// 所以使用它的进程必须以管理员身份运行。
/// </remarks>
public sealed class WfpDeviceClient : IDisposable
{
    private const int ErrorAccessDenied = 5;

    private readonly SafeFileHandle _handle;

    private WfpDeviceClient(SafeFileHandle handle) => _handle = handle;

    public static WfpDeviceClient Open()
    {
        DeviceLayout.Verify();

        var handle = NativeDeviceApi.CreateFile(
            NativeDeviceApi.DevicePath,
            NativeDeviceApi.GenericRead | NativeDeviceApi.GenericWrite,
            NativeDeviceApi.ShareRead | NativeDeviceApi.ShareWrite,
            IntPtr.Zero,
            NativeDeviceApi.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            var hint = error == ErrorAccessDenied
                              ? "（设备只允许 SYSTEM 与 Administrators：请以管理员身份运行）"
                              : "（驱动未加载？先 sc start wfprobe）";
            throw new Win32Exception(
                error,
                string.Create(CultureInfo.InvariantCulture,
                              $"打开 {NativeDeviceApi.DevicePath} 失败：Win32={error}{hint}"));
        }

        return new WfpDeviceClient(handle);
    }

    public void SetConfig(ProbeConfig config) =>
        Ioctl(ProbeIoctl.SetConfigCode, MemoryMarshal.AsBytes(new ReadOnlySpan<ProbeConfig>(in config)).ToArray(), 0);

    public ProbeConfig GetConfig() => ReadStruct<ProbeConfig>(ProbeIoctl.GetConfigCode, "GetConfig");

    public ProbeStats GetStats() => ReadStruct<ProbeStats>(ProbeIoctl.GetStatsCode, "GetStats");

    public ProbeRuleInfo GetRuleInfo() => ReadStruct<ProbeRuleInfo>(ProbeIoctl.GetRuleInfoCode, "GetRuleInfo");

    public void ResetStats() => Ioctl(ProbeIoctl.ResetStatsCode, [], 0);

    public void SetRuleBlob(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length == 0 || blob.Length > ProbeIoctl.RuleBlobMaxSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blob), blob.Length, $"规则 blob 长度必须在 1..{ProbeIoctl.RuleBlobMaxSize}");
        }

        Ioctl(ProbeIoctl.SetRuleBlobCode, blob, 0);
    }

    public void SetForwardedBlob(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length == 0 || blob.Length > ProbeIoctl.RuleBlobMaxSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(blob), blob.Length, $"forwarded blob 长度必须在 1..{ProbeIoctl.RuleBlobMaxSize}");
        }

        Ioctl(ProbeIoctl.SetForwardedBlobCode, blob, 0);
    }

    public ProbeForwardedInfo GetForwardedInfo() =>
        ReadStruct<ProbeForwardedInfo>(ProbeIoctl.GetForwardedInfoCode, "GetForwardedInfo");

    public ProbeForwardedStats GetForwardedStats() =>
        ReadStruct<ProbeForwardedStats>(ProbeIoctl.GetForwardedStatsCode, "GetForwardedStats");

    public void ResetForwardedStats() => Ioctl(ProbeIoctl.ResetForwardedStatsCode, [], 0);

    public void SetForwardedConfig(ProbeForwardedConfig config) =>
        Ioctl(ProbeIoctl.SetForwardedConfigCode,
              MemoryMarshal.AsBytes(new ReadOnlySpan<ProbeForwardedConfig>(in config)).ToArray(),
              0);

    public ProbeForwardedConfig GetForwardedConfig() =>
        ReadStruct<ProbeForwardedConfig>(ProbeIoctl.GetForwardedConfigCode, "GetForwardedConfig");


    public void SetForwardedIngressConfig(ProbeForwardedIngressConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Ioctl(ProbeIoctl.SetForwardedIngressConfigCode, config.ToBytes(), 0);
    }

    public ProbeForwardedIngressConfig GetForwardedIngressConfig()
    {
        var data = Ioctl(ProbeIoctl.GetForwardedIngressConfigCode, [],
                            ProbeForwardedIngressConfig.MaxBytes);
        return ProbeForwardedIngressConfig.FromBytes(data);
    }

    /// <summary>按 (listener 本地端点, client) 查 redirect 表；未命中时驱动返回 NOT_FOUND，这里抛 Win32Exception。</summary>

    public ProbeForwardedRedirect QueryForwardedRedirect(ProbeForwardedRedirectQuery query)
    {
        var input = MemoryMarshal.AsBytes(new ReadOnlySpan<ProbeForwardedRedirectQuery>(in query)).ToArray();
        var data = Ioctl(ProbeIoctl.QueryForwardedRedirectCode, input, DeviceLayout.ProbeForwardedRedirectSize);
        if (data.Length != DeviceLayout.ProbeForwardedRedirectSize)
        {
            throw new InvalidOperationException(
                $"QueryForwardedRedirect 返回 {data.Length} 字节，期望 {DeviceLayout.ProbeForwardedRedirectSize}");
        }
        return MemoryMarshal.Read<ProbeForwardedRedirect>(data);
    }
    public ProbeEvent[] GetEvents(int maxEvents)
    {
        if (maxEvents <= 0)
        {
            return [];
        }

        var size = maxEvents * Unsafe.SizeOf<ProbeEvent>();
        var data = Ioctl(ProbeIoctl.GetEventsCode, [], size);
        if (data.Length % Unsafe.SizeOf<ProbeEvent>() != 0)
        {
            throw new InvalidOperationException($"GetEvents 返回 {data.Length} 字节，不是 probe_event 的整数倍");
        }

        return MemoryMarshal.Cast<byte, ProbeEvent>(data.AsSpan()).ToArray();
    }

    /// <summary>诊断接口：把 UTF-16LE 字节交给内核的 collect_process_path，回读 UTF-8 长度与内容。</summary>
    public (uint Utf8Length, string Text) MeasureProcessPath(byte[] utf16Bytes)
    {
        ArgumentNullException.ThrowIfNull(utf16Bytes);
        if (utf16Bytes.Length < 2 || utf16Bytes.Length > ProbeIoctl.ProcessPathMeasureInputMax ||
            (utf16Bytes.Length % 2) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utf16Bytes), utf16Bytes.Length, "输入必须是 2..2048 之间的偶数字节");
        }

        var data = Ioctl(ProbeIoctl.MeasureProcessPathCode, utf16Bytes, DeviceLayout.ProcessPathMeasurementSize);
        if (data.Length != DeviceLayout.ProcessPathMeasurementSize)
        {
            throw new InvalidOperationException($"MeasureProcessPath 返回 {data.Length} 字节，期望 520");
        }

        var measurement = MemoryMarshal.Read<ProcessPathMeasurement>(data);
        var length = (int)Math.Min(measurement.Utf8Length, 512u);
        return (measurement.Utf8Length, Encoding.UTF8.GetString(data, 8, length));
    }

    /// <summary>原始 IOCTL 入口：只给测试用来验证驱动侧的缓冲区长度判据。</summary>
    internal byte[] RawIoctl(uint code, byte[] input, int outputSize) => Ioctl(code, input, outputSize);

    private T ReadStruct<T>(uint code, string what) where T : unmanaged
    {
        var size = Unsafe.SizeOf<T>();
        var data = Ioctl(code, [], size);
        if (data.Length != size)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"{what} 返回 {data.Length} 字节，期望 {size}"));
        }

        return MemoryMarshal.Read<T>(data);
    }

    private byte[] Ioctl(uint code, byte[] input, int outputSize)
    {
        var output = outputSize > 0 ? new byte[outputSize] : null;
        if (!NativeDeviceApi.DeviceIoControl(
                _handle, code, input.Length > 0 ? input : null, input.Length, output, outputSize, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                string.Create(CultureInfo.InvariantCulture,
                              $"DeviceIoControl 0x{code:X8} 失败：Win32={error}（{Describe(code)}）"));
        }

        if (returned == 0)
        {
            return [];
        }

        if (output is null || returned > output.Length)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture,
                              $"DeviceIoControl 0x{code:X8} 报告写回 {returned} 字节，超出输出缓冲区"));
        }

        return output.AsSpan(0, returned).ToArray();
    }

    private static string Describe(uint code) => code switch
    {
        ProbeIoctl.GetForwardedStatsCode => "GetForwardedStats",
        ProbeIoctl.SetForwardedConfigCode => "SetForwardedConfig",
        ProbeIoctl.GetForwardedConfigCode => "GetForwardedConfig",
        ProbeIoctl.SetForwardedIngressConfigCode => "SetForwardedIngressConfig",
        ProbeIoctl.GetForwardedIngressConfigCode => "GetForwardedIngressConfig",
        ProbeIoctl.QueryForwardedRedirectCode => "QueryForwardedRedirect",
        ProbeIoctl.SetConfigCode => "SetConfig",
        ProbeIoctl.SetRuleBlobCode => "SetRuleBlob",
        ProbeIoctl.GetStatsCode => "GetStats",
        ProbeIoctl.GetRuleInfoCode => "GetRuleInfo",
        ProbeIoctl.SetForwardedBlobCode => "SetForwardedBlob",
        ProbeIoctl.GetForwardedInfoCode => "GetForwardedInfo",
        _ => $"0x{code:X8}",
    };

    public void Dispose() => _handle.Dispose();
}
