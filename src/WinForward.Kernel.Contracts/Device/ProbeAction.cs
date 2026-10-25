namespace WinForward.Userland.Device;

/// <summary>最终动作，数值与 <c>src/common/rule_blob.h</c> 的 <c>rule_action</c> 一致。</summary>
public enum ProbeAction : byte
{
    Permit = 0,
    Block = 1,
    Redirect = 2,
}
