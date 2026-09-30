namespace WinForward.NdisApi;

/// <summary>
/// One read-only telemetry snapshot of <see cref="NdisApiDriver"/>'s read path, the instrument a
/// Windows run reads to decide which empty-queue semantics the pinned driver implements: flat
/// <c>FailedReads</c>/<c>QueueSizeQueries</c> with growing <c>EmptyReads</c> means a successful
/// read returned zero packets; <c>FailedReads</c> tracking <c>QueueSizeQueries</c> means the read
/// itself fails on an empty queue. The counters are seam-level call counts, never kernel round
/// trips.
/// </summary>
internal sealed record NdisReadDiagnostics(
    long BatchReads,
    long QueueSizeQueries,
    long EmptyReads,
    long FailedReads,
    int LastFailedReadNativeError,
    long ReadShapeMismatchCount);
