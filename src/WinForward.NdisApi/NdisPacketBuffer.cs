using System.Runtime.InteropServices;
using WinForward.Core;

namespace WinForward.NdisApi;

/// <summary>
/// A managed wrapper over one native <see cref="IntermediateBuffer"/>. Buffers created with the
/// public constructor are privately owned: <see cref="Dispose"/> frees the native memory. Buffers
/// rented from <see cref="NdisPacketBufferPool"/> keep pool ownership: <see cref="Dispose"/>
/// returns them to the pool instead of freeing, so <c>using</c>-style callers need no changes
/// when switching from per-injection allocation to pooling. A pooled buffer's Dispose contract is
/// rental-window-scoped: disposing again while the buffer sits idle in the pool is a no-op, but
/// once the pool has re-rented the buffer a stale Dispose flips it back to idle and returns it
/// under the current renter — a rental-contract violation whose effects are confined to that
/// renter (the pool itself never double-frees). Renters must Dispose exactly once per rental.
/// </summary>
public sealed unsafe class NdisPacketBuffer : IFrameSource, IDisposable
{
    private const int StateRented = 1;
    private const int StateIdle = 2;

    private IntermediateBuffer* _buffer;
    private NdisPacketBufferPool? _ownerPool;
    private int _pooledState;

    public NdisPacketBuffer()
    {
        _buffer = Allocate();
    }

    internal NdisPacketBuffer(NdisPacketBufferPool ownerPool)
    {
        _buffer = Allocate();
        _ownerPool = ownerPool;
        _pooledState = StateRented;
    }

    private static IntermediateBuffer* Allocate()
    {
        var pointer = (IntermediateBuffer*)NativeMemory.AllocZeroed((nuint)sizeof(IntermediateBuffer));
        if (pointer is null) throw new InvalidOperationException("Unable to allocate an NDISAPI packet buffer.");
        return pointer;
    }

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            return (nint)_buffer;
        }
    }
    public uint DeviceFlags => _buffer is null ? 0 : _buffer->DeviceFlags;
    public uint Flags => _buffer is null ? 0 : _buffer->Flags;
    public nint CapturedAdapterHandle => _buffer is null ? 0 : _buffer->AdapterHandle;
    public int Length => _buffer is null ? 0 : checked((int)_buffer->Length);

    internal bool IsRentedFrom(NdisPacketBufferPool pool) =>
        ReferenceEquals(_ownerPool, pool) && Volatile.Read(ref _pooledState) == StateRented;

    internal bool TryMarkRented() => Interlocked.CompareExchange(ref _pooledState, StateRented, StateIdle) == StateIdle;

    /// <summary>
    /// Detaches pool ownership and frees the native memory. Called only by the owning pool, for
    /// buffers it exclusively holds (returned past capacity, or drained at pool disposal).
    /// </summary>
    internal void ReleaseFromPool()
    {
        _ownerPool = null;
        var pointer = _buffer;
        _buffer = null;
        if (pointer is not null) NativeMemory.Free(pointer);
    }

    public Span<byte> GetFrame()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (_buffer->Length > NdisApiAbi.MaximumEthernetFrame) throw new InvalidDataException("NDISAPI returned a frame larger than the pinned ABI.");
        return new Span<byte>(_buffer->Buffer, checked((int)_buffer->Length));
    }

    /// <summary>
    /// Returns the buffer's full native frame storage for an in-place frame build: a caller
    /// writes the frame bytes into the span and then stamps the buffer with
    /// <see cref="CompleteFrame"/>, avoiding the copy that <see cref="SetFrame"/> performs. Unlike
    /// <see cref="GetFrame"/> the span is not bounded by the current length.
    /// </summary>
    public Span<byte> GetFrameStorage()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        return new Span<byte>(_buffer->Buffer, NdisApiAbi.MaximumEthernetFrame);
    }

    /// <summary>
    /// Completes an in-place frame build started through <see cref="GetFrameStorage"/>: stamps
    /// the frame length, the MSTCP-relative direction flag, and the enumeration adapter handle
    /// (the NDIS packet metadata flag starts zero, matching a fresh synthetic frame) without
    /// copying any frame bytes.
    /// </summary>
    public void CompleteFrame(int frameLength, uint deviceFlags, nint adapterHandle)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if ((uint)frameLength > NdisApiAbi.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frameLength));
        _buffer->AdapterHandle = adapterHandle;
        _buffer->UnionPadding = 0;
        _buffer->DeviceFlags = deviceFlags;
        _buffer->Length = (uint)frameLength;
        _buffer->Flags = 0;
    }

    int IFrameSource.FrameLength => Length;

    ReadOnlySpan<byte> IFrameSource.GetFrameSpan() => GetFrame();

    /// <summary>
    /// Marks an unmodified captured frame for reinjection in place: only the enumeration adapter
    /// handle is (re)targeted, so direction, length, flags, and payload stay exactly as captured.
    /// The reinjection call consumes the frame synchronously; afterwards the buffer returns to its
    /// owner's control (the capture pump's batch reuse ordering).
    /// </summary>
    public void PrepareForReinjection(nint adapterHandle)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        _buffer->AdapterHandle = adapterHandle;
        _buffer->UnionPadding = 0;
    }

    public void SetFrame(ReadOnlySpan<byte> frame, uint deviceFlags, nint adapterHandle)
        => SetFrame(frame, deviceFlags, adapterHandle, flags: 0);

    public void SetFrame(ReadOnlySpan<byte> frame, uint deviceFlags, nint adapterHandle, uint flags)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (frame.Length > NdisApiAbi.MaximumEthernetFrame) throw new ArgumentOutOfRangeException(nameof(frame));
        _buffer->AdapterHandle = adapterHandle;
        _buffer->UnionPadding = 0;
        _buffer->DeviceFlags = deviceFlags;
        _buffer->Length = (uint)frame.Length;
        _buffer->Flags = flags;
        frame.CopyTo(new Span<byte>(_buffer->Buffer, frame.Length));
    }

    public void Dispose()
    {
        if (_ownerPool is { } pool)
        {
            // A pooled buffer's Dispose returns it to its pool exactly once per rental window:
            // while the buffer sits idle (already returned) the CAS fails and the repeat Dispose
            // is a no-op; after a re-rental a stale Dispose succeeds under the current renter
            // (see the class doc's rental-window contract).
            if (Interlocked.CompareExchange(ref _pooledState, StateIdle, StateRented) == StateRented) pool.OnReturned(this);
            return;
        }
        if (_buffer is null) return;
        NativeMemory.Free(_buffer);
        _buffer = null;
    }
}
