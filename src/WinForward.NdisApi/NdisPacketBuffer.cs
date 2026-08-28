using System.Runtime.InteropServices;

namespace WinForward.NdisApi;

/// <summary>
/// A managed wrapper over one native <see cref="IntermediateBuffer"/>. Buffers created with the
/// public constructor are privately owned: <see cref="Dispose"/> frees the native memory. Buffers
/// rented from <see cref="NdisPacketBufferPool"/> keep pool ownership: <see cref="Dispose"/>
/// returns them to the pool instead of freeing, so <c>using</c>-style callers need no changes
/// when switching from per-injection allocation to pooling.
/// </summary>
public sealed unsafe class NdisPacketBuffer : IDisposable
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
            // A pooled buffer's Dispose returns it to its pool exactly once; a repeat Dispose of an
            // already-returned buffer is a no-op because the pool owns it now.
            if (Interlocked.CompareExchange(ref _pooledState, StateIdle, StateRented) == StateRented) pool.OnReturned(this);
            return;
        }
        if (_buffer is null) return;
        NativeMemory.Free(_buffer);
        _buffer = null;
    }
}
