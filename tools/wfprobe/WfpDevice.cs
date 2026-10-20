using System.ComponentModel;
using System.Runtime.InteropServices;
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), @"打开 \.\WfProbe 失败：驱动未加载，或当前用户没有访问该设备的权限（设备 DACL 只允许 SYSTEM 与 Administrators）");
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

            if (!DeviceIoControl(handle, code, inputBuffer, input.Length, outputBuffer, outputSize, out int returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"DeviceIoControl 0x{code:X8} 失败");

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
