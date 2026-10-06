using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinForward.NdisApi;

/// <summary>
/// NDISAPI driver wrapper. Native calls use a two-level gate topology (design D3 of task
/// 08-28-udp-loss-design-flaws): cold control operations (adapter enumeration, adapter mode
/// snapshot/set, close) share one control gate, while hot data operations (batched reads,
/// packet reinjection) serialize per adapter enumeration handle, so a slow IOCTL on one
/// adapter cannot stall every pump.
/// </summary>
/// <remarks>
/// The per-adapter split deliberately supersedes the single process-wide gate previously pinned
/// in .trellis/spec/backend/windows-ndisapi.md: the mutable-OVERLAPPED concern is scoped to each
/// request structure (all of them are method-local here), ndisrd already serves concurrent
/// client processes, and within one adapter handle every call remains serialized, preserving
/// per-adapter reinjection order.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NdisApiDriver : IDisposable, INdisPacketReader
{
    internal const int MaxStackMultiRequestBytes = 1024;

    // One batched send request spans 16 + 8*N bytes (ETH_M_REQUEST header plus one pointer slot
    // per packet); this chunk size keeps a full request inside the stackalloc budget above.
    internal const int MaxPacketsPerSendRequest = (MaxStackMultiRequestBytes - 16) / 8;

    private readonly NdisApiSafeHandle _handle;
    private readonly INdisReadPacketCalls _readCalls;
    private readonly NdisNativeCallGate _controlGate = new();
    private readonly NdisAdapterGateMap _adapterGates = new();
    // Adapter handles whose read-first drain already failed with a non-empty queue: they are served
    // by the query-first shape from then on (see IsQueryFirstForDiagnostics). Lock-free and
    // append-only — the same lifetime model as the gate map.
    private readonly ConcurrentDictionary<nint, byte> _queryFirstAdapters = new();

    private long _batchReads;
    private long _queueSizeQueries;
    private long _emptyReads;
    private long _failedReads;
    private long _readShapeMismatchCount;
    private int _lastFailedReadNativeError;

    private NdisApiDriver(NdisApiSafeHandle handle, INdisReadPacketCalls readCalls)
    {
        _handle = handle;
        _readCalls = readCalls;
    }

    public static NdisApiDriver Open()
    {
        NdisApiAbi.AssertManagedLayout();
        var rawHandle = NdisApiNative.OpenFilterDriver("NDISRD");
        var openError = Marshal.GetLastWin32Error();
        if (!NdisNativeCallStatus.HasValidNativeHandle(rawHandle)) NdisNativeCallStatus.ThrowIfOpenFailed(rawHandle, isDriverLoaded: false, openError);

        var handle = NdisApiSafeHandle.FromRawHandle(rawHandle);
        try
        {
            var isDriverLoaded = NdisApiNative.IsDriverLoaded(handle) != 0;
            var loadError = isDriverLoaded ? 0 : Marshal.GetLastWin32Error();
            if (!isDriverLoaded && loadError == 0) loadError = openError;
            NdisNativeCallStatus.ThrowIfOpenFailed(rawHandle, isDriverLoaded, loadError);
            return new NdisApiDriver(handle, new NdisNativeReadPacketCalls(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds a driver over an injected read-call seam and an invalid (zero) open handle, so the
    /// read path is reachable on a host without <c>ndisapi.dll</c>. A zero handle is invalid for
    /// <see cref="NdisApiSafeHandle"/>, so <see cref="Dispose"/> never reaches
    /// <c>NdisApiNative.CloseFilterDriver</c> and no fact can trip the DLL resolver.
    /// </summary>
    internal static NdisApiDriver CreateForTests(INdisReadPacketCalls readCalls)
    {
        ArgumentNullException.ThrowIfNull(readCalls);
        return new NdisApiDriver(NdisApiSafeHandle.FromRawHandle(0), readCalls);
    }

    public unsafe IReadOnlyList<NdisAdapter> GetAdapters()
    {
        TcpAdapterList native = default;
        using (_controlGate.Enter())
        {
            if (NdisApiNative.GetTcpipBoundAdaptersInfo(_handle, &native) == 0)
            {
                var nativeError = Marshal.GetLastWin32Error();
                throw new Win32Exception(nativeError, string.Create(CultureInfo.InvariantCulture, $"Unable to enumerate NDISAPI adapters (native error {nativeError}, 0x{nativeError:X8})."));
            }
        }

        var count = checked((int)Math.Min(native.AdapterCount, NdisApiAbi.AdapterListSize));
        var adapters = new List<NdisAdapter>(count);
        for (var index = 0; index < count; index++)
        {
            var name = ReadAscii(native.AdapterNames, index * NdisApiAbi.AdapterNameSize, NdisApiAbi.AdapterNameSize);
            var mac = new byte[NdisApiAbi.EthernetAddressLength];
            for (var octet = 0; octet < mac.Length; octet++) mac[octet] = native.CurrentAddresses[(index * mac.Length) + octet];
            adapters.Add(new NdisAdapter((nint)native.AdapterHandles[index], name, mac, native.Mtus[index]));
        }
        return adapters;
    }

    /// <summary>
    /// Registers or releases the driver's TCP/IP bound adapter-list-change notification
    /// (native <c>SetAdapterListChangeEvent</c>). While registered, the driver signals the
    /// caller-provided Win32 event whenever the bound adapter list is rebuilt (adapter
    /// plug/unplug, enable/disable, standby/resume) — every enumeration handle previously
    /// returned by <see cref="GetAdapters"/> is stale from that point and must be
    /// re-enumerated. Passing 0 (<see cref="nint.Zero"/>) releases the registration. The
    /// caller owns the event lifetime: the handle must remain valid for as long as the
    /// registration is active; the driver never closes it.
    /// </summary>
    /// <remarks>
    /// Cold-path control operation (one-time registration at startup, release at shutdown)
    /// routed through the control gate. A native FALSE throws <see cref="Win32Exception"/> —
    /// startup treats a registration failure as fatal because the in-process adapter
    /// refresh is unusable without the notification.
    /// </remarks>
    public void SetAdapterListChangeEvent(nint win32Event)
    {
        using var gateLease = _controlGate.Enter();
        if (NdisApiNative.SetAdapterListChangeEvent(_handle, win32Event) == 0)
        {
            var nativeError = Marshal.GetLastWin32Error();
            var operation = win32Event == nint.Zero ? "release" : "register";
            throw new Win32Exception(nativeError, string.Create(CultureInfo.InvariantCulture, $"Unable to {operation} the NDISAPI adapter-list-change event (native error {nativeError}, 0x{nativeError:X8})."));
        }
    }

    /// <summary>
    /// Registers an auto-reset Win32 event as this adapter's packet-arrival notification and returns
    /// the signal the pump waits on. The pinned header documents the driver as signalling the event
    /// while the adapter's packet queue is non-empty; the exact discipline (per arrival, per
    /// empty-to-non-empty transition, or level-ish) is a Windows open item, not something this host
    /// can establish. Auto-reset is what makes the arrangement safe to rely on: it coalesces bursts
    /// into one pending token and retains a signal raised while no waiter is parked, so a signal
    /// cannot be lost between loop iterations. Registration is best-effort by contract — a false
    /// return means the caller keeps the sleep-poll shape — so this never throws for a native failure
    /// or for a DLL without the export (in that case <paramref name="nativeError"/> is 0, because no
    /// native error exists). The returned signal owns the event and releases the registration on
    /// <see cref="INdisPacketArrivalSignal.Dispose"/>.
    /// </summary>
    public bool TryRegisterPacketEvent(nint adapterHandle, out INdisPacketArrivalSignal? signal, out int nativeError)
    {
        signal = null;
        nativeError = 0;
        EventWaitHandle? arrival = null;
        try
        {
            using var gateLease = _controlGate.Enter();
            arrival = new EventWaitHandle(initialState: false, EventResetMode.AutoReset);
            // The raw handle stays valid for the whole registration: the returned signal roots the
            // event and releases the registration before disposing it.
#pragma warning disable S3869 // Handing the raw event handle to the driver is the documented ABI here.
            var rawEvent = arrival.SafeWaitHandle.DangerousGetHandle();
#pragma warning restore S3869
            if (NdisApiNative.SetPacketEvent(_handle, adapterHandle, rawEvent) == 0)
            {
                nativeError = Marshal.GetLastWin32Error();
                arrival.Dispose();
                return false;
            }
        }
        catch (EntryPointNotFoundException)
        {
            arrival?.Dispose();
            return false;
        }

        signal = new NdisPacketArrivalSignal(arrival, () => ReleasePacketEvent(adapterHandle));
        return true;
    }

    /// <summary>
    /// Releases the adapter's packet-arrival registration (official NULL semantics). Best-effort: a
    /// stale-handle release during teardown has no remedy, exactly like the adapter-mode restore.
    /// </summary>
    private void ReleasePacketEvent(nint adapterHandle)
    {
        try
        {
            using var gateLease = _controlGate.Enter();
            NdisApiNative.SetPacketEvent(_handle, adapterHandle, nint.Zero);
        }
        catch (Exception exception)
        {
            GC.KeepAlive(exception);
        }
    }

    public unsafe uint GetAdapterMode(nint adapterHandle)
    {
        var mode = new AdapterMode { AdapterHandle = adapterHandle };
        using (_controlGate.Enter())
        {
            if (NdisApiNative.GetAdapterMode(_handle, &mode) == 0)
            {
                var nativeError = Marshal.GetLastWin32Error();
                throw new Win32Exception(nativeError, string.Create(CultureInfo.InvariantCulture, $"Unable to read NDISAPI adapter mode (native error {nativeError}, 0x{nativeError:X8})."));
            }
        }
        return mode.Flags;
    }

    public unsafe void SetAdapterMode(nint adapterHandle, uint flags)
    {
        var mode = new AdapterMode { AdapterHandle = adapterHandle, Flags = flags };
        using var gateLease = _controlGate.Enter();
        if (NdisApiNative.SetAdapterMode(_handle, &mode) == 0)
        {
            var nativeError = Marshal.GetLastWin32Error();
            throw new Win32Exception(nativeError, string.Create(CultureInfo.InvariantCulture, $"Unable to set NDISAPI adapter mode (native error {nativeError}, 0x{nativeError:X8})."));
        }
    }

    /// <summary>
    /// Reads up to one batch of packets from the adapter queue into the caller-provided buffers.
    /// The batched read is issued first and covers the whole caller array, so a successful read
    /// answers the empty-queue question itself and the steady-state drain costs a single native
    /// call; the queue query survives only as the disambiguator of a non-successful read, because
    /// the pinned <c>ReadPackets</c> BOOL cannot be shown on this host to separate "empty queue"
    /// from "driver error". Returns the number of buffers actually filled; 0 means the queue was
    /// empty. An unreadable queue, or a failed read from a queue the query reports non-empty,
    /// throws <see cref="Win32Exception"/>.
    /// </summary>
    /// <remarks>
    /// The first non-empty-queue read failure for an adapter handle is ambiguous — a genuine
    /// driver fault, or an ABI that refuses a request wider than the queue depth — so it arms
    /// <see cref="IsQueryFirstForDiagnostics"/> and retries once in the query-first shape instead
    /// of entering the caller's transient-retry budget. See <see cref="ReadShapeMismatchSink"/>.
    /// </remarks>
    public int TryReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        if (buffers.Length == 0) return 0;

        var adapterGate = _adapterGates.Get(adapterHandle);
        using (adapterGate.Enter())
        {
            return IsQueryFirstForDiagnostics(adapterHandle)
                ? ReadQueryFirst(adapterHandle, buffers)
                : ReadSpeculatively(adapterHandle, buffers);
        }
    }

    /// <summary>
    /// The historical shape, kept for an adapter the mismatch guard healed: the query sizes and
    /// gates the read, so an empty queue costs one query and never a read. The read's failure is
    /// unambiguous here — the query just reported the queue non-empty.
    /// </summary>
    private int ReadQueryFirst(nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        var querySucceeded = TryQueryQueueSize(adapterHandle, out var queuedPacketCount, out var queryError);
        if (!NdisNativeCallStatus.HasQueuedPackets(querySucceeded ? 1 : 0, queryError, queuedPacketCount, adapterHandle)) return EmptyDrain();

        var requestedCount = ClampToCapacity(queuedPacketCount, buffers.Length);
        if (!TryReadBatch(adapterHandle, buffers, requestedCount, out var packetsSuccess, out var readError))
        {
            NdisNativeCallStatus.ThrowReadFailedOnNonEmptyQueue(queuedPacketCount, requestedCount, readError, adapterHandle);
        }

        var count = NdisNativeCallStatus.ClampReadCount(packetsSuccess, requestedCount);
        return count == 0 ? EmptyDrain() : count;
    }

    /// <summary>
    /// The read-first shape: one full-capacity read, and a query only when that read did not
    /// succeed. Zero packets from a successful read is the empty queue — no second call.
    /// </summary>
    private int ReadSpeculatively(nint adapterHandle, NdisPacketBuffer[] buffers)
    {
        var requestedCount = buffers.Length;
        if (TryReadBatch(adapterHandle, buffers, requestedCount, out var packetsSuccess, out var readError))
        {
            var count = NdisNativeCallStatus.ClampReadCount(packetsSuccess, requestedCount);
            return count == 0 ? EmptyDrain() : count;
        }

        var querySucceeded = TryQueryQueueSize(adapterHandle, out var queuedPacketCount, out var queryError);
        if (!NdisNativeCallStatus.HasQueuedPackets(querySucceeded ? 1 : 0, queryError, queuedPacketCount, adapterHandle)) return EmptyDrain();

        // The queue is non-empty and the full-capacity request still failed. Arming is once per
        // adapter handle, so a second observation is a real fault and takes today's throw.
        if (!ArmQueryFirst(adapterHandle, requestedCount, queuedPacketCount, readError))
        {
            NdisNativeCallStatus.ThrowReadFailedOnNonEmptyQueue(queuedPacketCount, requestedCount, readError, adapterHandle);
        }

        // The disambiguating query is also the query-first attempt's own query, so the probe is one
        // extra read, not a query+read pair.
        var probeCount = ClampToCapacity(queuedPacketCount, requestedCount);
        if (!TryReadBatch(adapterHandle, buffers, probeCount, out packetsSuccess, out readError))
        {
            NdisNativeCallStatus.ThrowReadFailedOnNonEmptyQueue(queuedPacketCount, probeCount, readError, adapterHandle);
        }

        var probeSuccess = NdisNativeCallStatus.ClampReadCount(packetsSuccess, probeCount);
        return probeSuccess == 0 ? EmptyDrain() : probeSuccess;
    }

    /// <summary>
    /// Whether this adapter handle is served by the query-first shape because its first read-first
    /// drain failed while the queue was non-empty. Sticky for the driver's lifetime, keyed by the
    /// enumeration handle exactly like the gate map (a refresh that reuses a handle value inherits
    /// it). Exposed for the seam facts and for a Windows run's read-out.
    /// </summary>
    internal bool IsQueryFirstForDiagnostics(nint adapterHandle) => _queryFirstAdapters.ContainsKey(adapterHandle);

    /// <summary>
    /// One read-only telemetry snapshot of this driver's read path (see
    /// <see cref="NdisReadDiagnostics"/>). The counter increment sites are part of the instrument:
    /// they are what separates the two empty-queue ABI hypotheses on a real driver.
    /// </summary>
    internal NdisReadDiagnostics ReadDiagnostics => new(
        Interlocked.Read(ref _batchReads),
        Interlocked.Read(ref _queueSizeQueries),
        Interlocked.Read(ref _emptyReads),
        Interlocked.Read(ref _failedReads),
        Volatile.Read(ref _lastFailedReadNativeError),
        Interlocked.Read(ref _readShapeMismatchCount));

    /// <summary>
    /// Receives one observation per adapter handle whose first non-empty-queue read failure armed
    /// the query-first shape. Set once per capture generation by the composition, which holds the
    /// adapter identity and the logger this assembly cannot reach; null disables publication.
    /// </summary>
    public Action<NdisReadShapeMismatch>? ReadShapeMismatchSink
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    /// <summary>
    /// Arms the sticky query-first shape for <paramref name="adapterHandle"/> and publishes the
    /// observation. False means the handle was already armed — the caller then takes the throw path
    /// without probing.
    /// </summary>
    private bool ArmQueryFirst(nint adapterHandle, int requestedCount, uint queuedPacketCount, int readError)
    {
        if (!_queryFirstAdapters.TryAdd(adapterHandle, 0)) return false;
        Interlocked.Increment(ref _readShapeMismatchCount);
        ReadShapeMismatchSink?.Invoke(new NdisReadShapeMismatch(adapterHandle, requestedCount, queuedPacketCount, readError));
        return true;
    }

    private bool TryReadBatch(nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError)
    {
        var succeeded = _readCalls.ReadPackets(adapterHandle, buffers, count, out packetsSuccess, out nativeError);
        Interlocked.Increment(ref _batchReads);
        if (succeeded) return true;
        // Captured before any classification or throw: a Windows run reads this pair to tell a
        // FALSE-on-empty driver from a TRUE-with-zero one.
        Interlocked.Increment(ref _failedReads);
        Interlocked.Exchange(ref _lastFailedReadNativeError, nativeError);
        return false;
    }

    private bool TryQueryQueueSize(nint adapterHandle, out uint queuedPacketCount, out int nativeError)
    {
        var succeeded = _readCalls.GetAdapterPacketQueueSize(adapterHandle, out queuedPacketCount, out nativeError);
        Interlocked.Increment(ref _queueSizeQueries);
        return succeeded;
    }

    private int EmptyDrain()
    {
        Interlocked.Increment(ref _emptyReads);
        return 0;
    }

    private static int ClampToCapacity(uint queuedPacketCount, int capacity) =>
        (int)Math.Min(queuedPacketCount, (uint)capacity);

    // Fills the request header and the packet-pointer slots [0, count) from buffers[offset, offset+count).
    // Visible to tests for direct ABI-layer slot verification (same layout discipline as NdisApiAbiTests).
    internal static unsafe void BuildMultiRequest(byte* requestMemory, nint adapterHandle, NdisPacketBuffer[] buffers, int count, int offset)
    {
        var request = (EthernetMultiRequest*)requestMemory;
        request->AdapterHandle = adapterHandle;
        request->PacketsNumber = (uint)count;
        request->PacketsSuccess = 0;
        var slots = (NdisrdEthernetPacket*)&request->FirstBuffer;
        for (var index = 0; index < count; index++)
        {
            var buffer = buffers[offset + index] ?? throw new ArgumentNullException(nameof(buffers));
            slots[index] = new NdisrdEthernetPacket { Buffer = buffer.Pointer };
        }
    }

    internal static unsafe nuint MultiRequestByteCount(int count) =>
        (nuint)sizeof(EthernetMultiRequest) + ((nuint)(count - 1) * (nuint)sizeof(NdisrdEthernetPacket));

    public unsafe void SendPacketToMstcp(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var request = new EthernetRequest { AdapterHandle = adapterHandle, Packet = new NdisrdEthernetPacket { Buffer = buffer.Pointer } };
        var adapterGate = _adapterGates.Get(adapterHandle);
        using var gateLease = adapterGate.Enter();
        if (NdisApiNative.SendPacketToMstcp(_handle, &request) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, string.Create(CultureInfo.InvariantCulture, $"Unable to inject an NDISAPI packet toward MSTCP (native error {error}, length {buffer.Length}, device flags 0x{buffer.DeviceFlags:X}, NDIS flags 0x{buffer.Flags:X}, adapter 0x{adapterHandle:X})."));
        }
    }

    public unsafe void SendPacketToAdapter(nint adapterHandle, NdisPacketBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var request = new EthernetRequest { AdapterHandle = adapterHandle, Packet = new NdisrdEthernetPacket { Buffer = buffer.Pointer } };
        var adapterGate = _adapterGates.Get(adapterHandle);
        using var gateLease = adapterGate.Enter();
        if (NdisApiNative.SendPacketToAdapter(_handle, &request) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, string.Create(CultureInfo.InvariantCulture, $"Unable to inject an NDISAPI packet toward the adapter (native error {error}, length {buffer.Length}, device flags 0x{buffer.DeviceFlags:X}, NDIS flags 0x{buffer.Flags:X}, adapter 0x{adapterHandle:X})."));
        }
    }

    /// <summary>
    /// Injects <paramref name="count"/> packets from <paramref name="buffers"/> toward MSTCP in one
    /// batched request (chunks of at most <see cref="MaxPacketsPerSendRequest"/> keep each
    /// ETH_M_REQUEST inside the stackalloc budget; one adapter-gate lease spans the whole call).
    /// The batched send IOCTLs report no per-packet success count — the user-mode DLL passes no
    /// output buffer, so <c>dwPacketsSuccess</c> never returns (task 08-30-batched-ioctls research:
    /// wiresock/ndisapi@417b8734 ndisapi.cpp + local DLL disassembly) — so a failed batch throws
    /// with the same fail-closed semantics as a failed single send.
    /// </summary>
    public void SendPacketsToMstcp(nint adapterHandle, NdisPacketBuffer[] buffers, int count) =>
        SendPacketsBatch(adapterHandle, buffers, count, toMstcp: true);

    /// <summary>
    /// Injects <paramref name="count"/> packets from <paramref name="buffers"/> toward the adapter
    /// in one batched request. See <see cref="SendPacketsToMstcp(nint, NdisPacketBuffer[], int)"/>
    /// for the chunking, gate-lease, and all-or-nothing failure contract.
    /// </summary>
    public void SendPacketsToAdapter(nint adapterHandle, NdisPacketBuffer[] buffers, int count) =>
        SendPacketsBatch(adapterHandle, buffers, count, toMstcp: false);

    private unsafe void SendPacketsBatch(nint adapterHandle, NdisPacketBuffer[] buffers, int count, bool toMstcp)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffers.Length);
        if (count == 0) return;

        var adapterGate = _adapterGates.Get(adapterHandle);
        using var gateLease = adapterGate.Enter();
        var chunkCapacity = Math.Min(count, MaxPacketsPerSendRequest);
        var requestBytes = stackalloc byte[(int)MultiRequestByteCount(chunkCapacity)];
        for (var offset = 0; offset < count; offset += chunkCapacity)
        {
            var chunkCount = Math.Min(chunkCapacity, count - offset);
            BuildMultiRequest(requestBytes, adapterHandle, buffers, chunkCount, offset);
            var request = (EthernetMultiRequest*)requestBytes;
            var result = toMstcp
                ? NdisApiNative.SendPacketsToMstcp(_handle, request)
                : NdisApiNative.SendPacketsToAdapter(_handle, request);
            if (result == 0)
            {
                var error = Marshal.GetLastWin32Error();
                var target = toMstcp ? "MSTCP" : "the adapter";
                throw new Win32Exception(error, string.Create(CultureInfo.InvariantCulture, $"Unable to inject {chunkCount} NDISAPI packets toward {target} (native error {error}, packets {offset}..{offset + chunkCount - 1} of {count}, adapter 0x{adapterHandle:X})."));
            }
        }
    }

    public void Dispose()
    {
        using var gateLease = _controlGate.Enter();
        _handle.Dispose();
    }

    private static unsafe string ReadAscii(byte* source, int offset, int capacity)
    {
        var length = 0;
        while (length < capacity && source[offset + length] != 0) length++;
        return System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(source + offset, length));
    }
}
