namespace WinForward.Userland.Device;

/// <summary>
/// 控制面 IOCTL 的常量，逐项对应 <c>src/driver/probe_ioctl.h</c>。
/// </summary>
/// <remarks>
/// 权威定义在 C++ 头里。这个文件是产品侧的唯一一份镜像；每个数值都由
/// <c>WinForward.Userland.Tests</c> 的 ABI 一致性测试从头的文本里读回来核对
/// （见 <c>AbiConsistencyTests</c>），只改一侧会让那条测试变红。
///
/// <c>tools/wfprobe/Protocol.cs</c> 是测试夹具的第三份拷贝，同一组测试也锁住它，
/// 因此三处不可能各自漂移。
/// </remarks>
public static class ProbeIoctl
{
    /// <summary>ABI 版本。任何字段调整都必须同时提升它。</summary>
    public const uint AbiVersion = 3;

    /// <summary>self-PID 集合容量（防循环闸 4）。</summary>
    public const uint MaxSelfProcessIds = 8;

    /// <summary>单块规则 blob 的上限。</summary>
    public const uint RuleBlobMaxSize = 64 * 1024;

    /// <summary>诊断接口 <see cref="MeasureProcessPathCode"/> 的输入上限（UTF-16 字节数）。</summary>
    public const uint ProcessPathMeasureInputMax = 2048;

    // CTL_CODE(FILE_DEVICE_UNKNOWN, function, METHOD_BUFFERED, FILE_ANY_ACCESS)：
    //   (device << 16) | (access << 14) | (function << 2) | method
    private const uint FileDeviceUnknown = 0x22;
    private const uint MethodBuffered = 0;
    private const uint FileAnyAccess = 0;

    public const uint SetConfigFunction = 0x800;
    public const uint GetConfigFunction = 0x801;
    public const uint GetStatsFunction = 0x802;
    public const uint ResetStatsFunction = 0x803;
    public const uint GetEventsFunction = 0x804;
    public const uint SetRuleBlobFunction = 0x805;
    public const uint GetRuleInfoFunction = 0x806;
    public const uint MeasureProcessPathFunction = 0x807;

    public const uint SetConfigCode = (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (SetConfigFunction << 2) | MethodBuffered;
    public const uint GetConfigCode = (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (GetConfigFunction << 2) | MethodBuffered;
    public const uint GetStatsCode = (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (GetStatsFunction << 2) | MethodBuffered;
    public const uint ResetStatsCode = (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (ResetStatsFunction << 2) | MethodBuffered;
    public const uint GetEventsCode = (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (GetEventsFunction << 2) | MethodBuffered;
    public const uint SetRuleBlobCode = (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (SetRuleBlobFunction << 2) | MethodBuffered;
    public const uint GetRuleInfoCode = (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (GetRuleInfoFunction << 2) | MethodBuffered;
    public const uint MeasureProcessPathCode = (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (MeasureProcessPathFunction << 2) | MethodBuffered;

    /// <summary>与 C++ 侧 <c>make_ioctl</c> 相同的计算方式，测试用它交叉核对上面的常量。</summary>
    public static uint MakeIoctl(uint function) =>
        (FileDeviceUnknown << 16) | (FileAnyAccess << 14) | (function << 2) | MethodBuffered;
}
