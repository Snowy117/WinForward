namespace WinForward.NdisApi;

public sealed record NdisAdapter(nint RuntimeHandle, string InternalName, byte[] MacAddress, ushort Mtu);
