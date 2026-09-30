namespace WinForward.NdisApi;

/// <summary>
/// One observation of the ambiguity the read-first shape introduces: a full-capacity
/// <c>ReadPackets</c> request failed while the queue query reported packets available. That is
/// either a genuine driver fault or an ABI that refuses a request wider than the queue depth, and
/// the first such observation per adapter handle arms the query-first shape instead of degrading a
/// working adapter. Published once per arming through
/// <see cref="NdisApiDriver.ReadShapeMismatchSink"/>, which the composition turns into the
/// rate-limited <c>adapter.readShape.mismatch</c> diagnostic; its presence in a Windows run's log
/// is what tells the two ABI behaviours apart.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly record struct NdisReadShapeMismatch(nint AdapterHandle, int RequestedCount, uint QueuedPacketCount, int NativeError);
