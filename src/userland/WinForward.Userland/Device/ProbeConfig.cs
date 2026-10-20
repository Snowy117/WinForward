using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinForward.Userland.Device;

/// <summary>
/// <c>probe_config</c> 的逐字段镜像：64 字节，Pack = 4。字段名与 C++ 侧一一对应（命名风格各自遵守本语言规范）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ProbeConfig
{
    public uint AbiVersion;
    public uint Enabled;
    public uint ListenerProcessId;
    public ushort ListenerPort;
    public ushort Reserved0;
    public uint SelfProcessCount;
    public ProcessIdSlots SelfProcessIds;
    public ReservedSlots Reserved;

    [InlineArray(8)]
    public struct ProcessIdSlots
    {
        private uint _element0;
    }

    [InlineArray(3)]
    public struct ReservedSlots
    {
        private uint _element0;
    }

    /// <summary>关闭重定向的配置：驱动在 <c>enabled == 0</c> 时只放行、不求值规则。</summary>
    public static ProbeConfig Disabled() => new() { AbiVersion = ProbeIoctl.AbiVersion };

    /// <summary>
    /// 构造启用状态的控制面配置。校验与驱动 <c>apply_config</c> 的接收条件一致，
    /// 让非法组合在本进程内先失败，而不是变成一个 Win32 错误码。
    /// </summary>
    public static ProbeConfig EnabledFor(uint listenerProcessId, ushort listenerPort, ReadOnlySpan<uint> selfProcessIds)
    {
        if (listenerProcessId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(listenerProcessId), listenerProcessId, "监听进程 PID 不能为 0");
        }

        if (listenerPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(listenerPort), listenerPort, "监听端口不能为 0");
        }

        if (selfProcessIds.Length > ProbeIoctl.MaxSelfProcessIds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selfProcessIds), selfProcessIds.Length, $"self-PID 集合最多 {ProbeIoctl.MaxSelfProcessIds} 个");
        }

        ProbeConfig config = default;
        config.AbiVersion = ProbeIoctl.AbiVersion;
        config.Enabled = 1;
        config.ListenerProcessId = listenerProcessId;
        config.ListenerPort = listenerPort;
        config.SelfProcessCount = (uint)selfProcessIds.Length;
        for (int index = 0; index < selfProcessIds.Length; index++)
        {
            uint processId = selfProcessIds[index];
            if (processId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(selfProcessIds), index, "self-PID 不允许为 0");
            }

            config.SelfProcessIds[index] = processId;
        }

        return config;
    }
}
