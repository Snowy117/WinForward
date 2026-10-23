using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinForward.Probe;

internal sealed class WfpDevice : IDisposable
{
    private const string DevicePath = @"\\.\WfProbe";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private readonly SafeFileHandle handle;

    public WfpDevice()
    {
        handle = CreateFileW(DevicePath, GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0,
                             IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                                     @"打开 \.\WfProbe 失败：驱动未加载，或当前用户没有访问该设备的权限（设备 DACL 只允许 SYSTEM 与 Administrators）");
    }

    public unsafe void SetConfig(ProbeConfig config)
    {
        ReadOnlySpan<byte> bytes = new(&config, sizeof(ProbeConfig));
        Ioctl(Protocol.SetConfigCode, bytes, 0);
    }

    public unsafe ProbeConfig GetConfig()
    {
        byte[] data = Ioctl(Protocol.GetConfigCode, ReadOnlySpan<byte>.Empty, sizeof(ProbeConfig));
        if (data.Length != sizeof(ProbeConfig))
            throw new InvalidOperationException($"GetConfig 返回 {data.Length} 字节，期望 {sizeof(ProbeConfig)}");
        return MemoryMarshal.Read<ProbeConfig>(data);
    }

    public ProbeStats GetStats()
    {
        byte[] data = Ioctl(Protocol.GetStatsCode, ReadOnlySpan<byte>.Empty, Marshal.SizeOf<ProbeStats>());
        if (data.Length != Marshal.SizeOf<ProbeStats>())
            throw new InvalidOperationException($"GetStats 返回 {data.Length} 字节");
        return MemoryMarshal.Read<ProbeStats>(data);
    }

    public void ResetStats() => Ioctl(Protocol.ResetStatsCode, ReadOnlySpan<byte>.Empty, 0);

    public unsafe ProbeEvent[] GetEvents(int maxEvents)
    {
        int size = maxEvents * sizeof(ProbeEvent);
        byte[] data = Ioctl(Protocol.GetEventsCode, ReadOnlySpan<byte>.Empty, size);
        var events = new ProbeEvent[data.Length / sizeof(ProbeEvent)];
        MemoryMarshal.Cast<byte, ProbeEvent>(data.AsSpan()).CopyTo(events);
        return events;
    }

    public void SetRuleBlob(byte[] blob) => Ioctl(Protocol.SetRuleBlobCode, blob, 0);

    public void SetForwardedBlob(byte[] blob)
    {
        if (blob.Length == 0 || blob.Length > Protocol.RuleBlobMaxSize)
            throw new ArgumentOutOfRangeException(nameof(blob), blob.Length, $"forwarded blob 长度必须在 1..{Protocol.RuleBlobMaxSize}");
        Ioctl(Protocol.SetForwardedBlobCode, blob, 0);
    }

    public ProbeForwardedInfo GetForwardedInfo()
    {
        int size = Marshal.SizeOf<ProbeForwardedInfo>();
        byte[] data = Ioctl(Protocol.GetForwardedInfoCode, ReadOnlySpan<byte>.Empty, size);
        if (data.Length != size)
            throw new InvalidOperationException($"GetForwardedInfo 返回 {data.Length} 字节，期望 {size}");
        return MemoryMarshal.Read<ProbeForwardedInfo>(data);
    }

    public ProbeForwardedStats GetForwardedStats()
    {
        int size = Marshal.SizeOf<ProbeForwardedStats>();
        byte[] data = Ioctl(Protocol.GetForwardedStatsCode, ReadOnlySpan<byte>.Empty, size);
        if (data.Length != size)
            throw new InvalidOperationException($"GetForwardedStats 返回 {data.Length} 字节，期望 {size}");
        return MemoryMarshal.Read<ProbeForwardedStats>(data);
    }

    public void ResetForwardedStats() => Ioctl(Protocol.ResetForwardedStatsCode, ReadOnlySpan<byte>.Empty, 0);

    public unsafe void SetForwardedConfig(ProbeForwardedConfig config)
    {
        ReadOnlySpan<byte> bytes = new(&config, sizeof(ProbeForwardedConfig));
        Ioctl(Protocol.SetForwardedConfigCode, bytes, 0);
    }

    public unsafe ProbeForwardedConfig GetForwardedConfig()
    {
        byte[] data = Ioctl(Protocol.GetForwardedConfigCode, ReadOnlySpan<byte>.Empty,
                            sizeof(ProbeForwardedConfig));
        if (data.Length != sizeof(ProbeForwardedConfig))
            throw new InvalidOperationException(
                $"GetForwardedConfig 返回 {data.Length} 字节，期望 {sizeof(ProbeForwardedConfig)}");
        return MemoryMarshal.Read<ProbeForwardedConfig>(data);
    }
    public unsafe void SetForwardedIngressConfig(bool enabled, IReadOnlyList<ProbeForwardedIngress> ingresses)
    {
        ArgumentNullException.ThrowIfNull(ingresses);
        if (ingresses.Count > Protocol.ForwardedIngressCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(ingresses), ingresses.Count,
                                                  $"入口记录不能超过 {Protocol.ForwardedIngressCapacity}");
        }

        int headerSize = sizeof(ProbeForwardedIngressConfig);
        int ingressSize = sizeof(ProbeForwardedIngress);
        byte[] bytes = new byte[headerSize + (ingresses.Count * ingressSize)];
        ProbeForwardedIngressConfig header = default;
        header.AbiVersion = Protocol.AbiVersion;
        header.Enabled = enabled ? 1u : 0u;
        header.IngressCount = (uint)ingresses.Count;
        MemoryMarshal.Write(bytes.AsSpan(0, headerSize), in header);
        for (int index = 0; index < ingresses.Count; index++)
        {
            ProbeForwardedIngress ingress = ingresses[index];
            MemoryMarshal.Write(bytes.AsSpan(headerSize + (index * ingressSize), ingressSize), in ingress);

        }

        Ioctl(Protocol.SetForwardedIngressConfigCode, bytes, 0);
    }

    public unsafe (bool Enabled, ProbeForwardedIngress[] Ingresses) GetForwardedIngressConfig()
    {
        int headerSize = sizeof(ProbeForwardedIngressConfig);
        int ingressSize = sizeof(ProbeForwardedIngress);
        int maxBytes = headerSize + ((int)Protocol.ForwardedIngressCapacity * ingressSize);
        byte[] data = Ioctl(Protocol.GetForwardedIngressConfigCode, ReadOnlySpan<byte>.Empty, maxBytes);
        if (data.Length < headerSize)
        {
            throw new InvalidOperationException($"GetForwardedIngressConfig 返回 {data.Length} 字节，不足头部 {headerSize}");
        }

        ProbeForwardedIngressConfig header = MemoryMarshal.Read<ProbeForwardedIngressConfig>(data);
        if (header.IngressCount > Protocol.ForwardedIngressCapacity)
        {
            throw new InvalidOperationException($"入口记录数 {header.IngressCount} 超过上限 {Protocol.ForwardedIngressCapacity}");
        }

        int expected = headerSize + ((int)header.IngressCount * ingressSize);
        if (data.Length < expected)
        {
            throw new InvalidOperationException($"GetForwardedIngressConfig 返回 {data.Length} 字节，{header.IngressCount} 条记录需要 {expected}");
        }

        var ingresses = new ProbeForwardedIngress[header.IngressCount];
        for (int index = 0; index < ingresses.Length; index++)
        {
            ingresses[index] = MemoryMarshal.Read<ProbeForwardedIngress>(
                data.AsSpan(headerSize + (index * ingressSize), ingressSize));
        }

        return (header.Enabled != 0, ingresses);
    }


    public unsafe ProbeForwardedRedirect QueryForwardedRedirect(ProbeForwardedRedirectQuery query)
    {
        ReadOnlySpan<byte> input = new(&query, sizeof(ProbeForwardedRedirectQuery));
        byte[] data = Ioctl(Protocol.QueryForwardedRedirectCode, input, sizeof(ProbeForwardedRedirect));
        if (data.Length != sizeof(ProbeForwardedRedirect))
            throw new InvalidOperationException(
                $"QueryForwardedRedirect 返回 {data.Length} 字节，期望 {sizeof(ProbeForwardedRedirect)}");
        return MemoryMarshal.Read<ProbeForwardedRedirect>(data);
    }

    // legacy v6 查询：24 字节输入（不含 L）+ 64 字节 v1 输出。用于验证旧尺寸仍能查到表项。
    public unsafe ProbeForwardedRedirect QueryForwardedRedirectLegacy(ProbeForwardedRedirectQuery query)
    {
        ReadOnlySpan<byte> input = new(&query, 24);
        byte[] data = Ioctl(Protocol.QueryForwardedRedirectCode, input, 64);
        if (data.Length != 64)
        {
            throw new InvalidOperationException($"legacy RedirectQuery 返回 {data.Length} 字节，期望 64");
        }

        byte[] padded = new byte[sizeof(ProbeForwardedRedirect)];
        data.CopyTo(padded, 0);
        return MemoryMarshal.Read<ProbeForwardedRedirect>(padded);
    }


    // 诊断：把 UTF-16LE 字节交给内核的 collect_process_path，回读 UTF-8 长度与内容。
    public (uint Utf8Length, string Text) MeasureProcessPath(byte[] utf16)
    {
        byte[] data = Ioctl(Protocol.MeasureProcessPathCode, utf16, 520);
        if (data.Length != 520)
            throw new InvalidOperationException($"MeasureProcessPath 返回 {data.Length} 字节");
        uint length = MemoryMarshal.Read<uint>(data);
        int safeLength = (int)Math.Min(length, 512u);
        return (length, Encoding.UTF8.GetString(data, 8, safeLength));
    }

    public ProbeRuleInfo GetRuleInfo()
    {
        byte[] data = Ioctl(Protocol.GetRuleInfoCode, ReadOnlySpan<byte>.Empty, Marshal.SizeOf<ProbeRuleInfo>());
        if (data.Length != Marshal.SizeOf<ProbeRuleInfo>())
            throw new InvalidOperationException($"GetRuleInfo 返回 {data.Length} 字节");
        return MemoryMarshal.Read<ProbeRuleInfo>(data);
    }

    private unsafe byte[] Ioctl(uint code, ReadOnlySpan<byte> input, int outputSize)
    {
        IntPtr inputBuffer = IntPtr.Zero;
        IntPtr outputBuffer = IntPtr.Zero;
        try
        {
            if (input.Length > 0)
            {
                inputBuffer = Marshal.AllocHGlobal(input.Length);
                input.CopyTo(new Span<byte>((void*)inputBuffer, input.Length));
            }

            if (outputSize > 0)
                outputBuffer = Marshal.AllocHGlobal(outputSize);

            if (!DeviceIoControl(handle, code, inputBuffer, input.Length, outputBuffer, outputSize, out int returned,
                                 IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"DeviceIoControl 0x{code:X8} 失败（Win32={error}）");
            }

            byte[] result = new byte[returned];
            if (returned > 0)
                Marshal.Copy(outputBuffer, result, 0, returned);
            return result;
        }
        finally
        {
            if (outputBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(outputBuffer);
            if (inputBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(inputBuffer);
        }
    }

    public void Dispose() => handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName,
                                                     uint desiredAccess,
                                                     uint shareMode,
                                                     IntPtr securityAttributes,
                                                     uint creationDisposition,
                                                     uint flagsAndAttributes,
                                                     IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device,
                                               uint ioControlCode,
                                               IntPtr inBuffer,
                                               int inBufferSize,
                                               IntPtr outBuffer,
                                               int outBufferSize,
                                               out int bytesReturned,
                                               IntPtr overlapped);
}
