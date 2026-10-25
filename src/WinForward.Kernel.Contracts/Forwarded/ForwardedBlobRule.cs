using System.Runtime.InteropServices;

namespace WinForward.Userland.Forwarded;

/// <summary>forwarded_blob.h 中 <c>forwarded_blob_rule</c> 的逐字段镜像：24 字节，Pack = 4。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ForwardedBlobRule
{
    public uint IngressAdapterId;
    public uint ClientNetworkIndex;
    public uint RemoteNetworkIndex;
    public ushort RemotePortStart;
    public ushort RemotePortEnd;
    public uint UpstreamId;
    public byte Protocol;
    public byte Family;
    public byte Action;
    public byte Reserved;
}
