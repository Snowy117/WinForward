namespace WinForward.NdisApi;

public sealed record NdisAdapter(nint RuntimeHandle, string InternalName, uint Medium, byte[] MacAddress, ushort Mtu);
