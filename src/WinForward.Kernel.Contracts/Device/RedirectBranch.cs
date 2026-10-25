namespace WinForward.Userland.Device;

/// <summary>驱动选中的重定向目标分支，数值对应 <c>probe_ioctl.h</c> 的 <c>redirect_branch</c>。</summary>
public enum RedirectBranch : byte
{
    None = 0,
    AnyToLoopback = 1,
    LocalAddress = 2,
}
