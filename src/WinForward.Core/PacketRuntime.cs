namespace WinForward.Core;

public enum PacketDisposition
{
    Pass,
    Block,
    ProxyConsumed
}

public sealed class PacketLease : IDisposable
{
    private readonly Action<ReadOnlyMemory<byte>>? _onCompleted;
    private int _completed;

    public PacketLease(ReadOnlyMemory<byte> frame) => Frame = frame;

    /// <summary>
    /// Creates a lease whose frame is returned to its owner (for example an array pool) exactly
    /// once, on the lease's unique completion path. The callback fires inside
    /// <see cref="TryComplete(PacketDisposition)"/> (including the <see cref="Dispose"/> path), so
    /// any frame read must happen before completion when this constructor is used.
    /// </summary>
    public PacketLease(ReadOnlyMemory<byte> frame, Action<ReadOnlyMemory<byte>>? onCompleted)
    {
        Frame = frame;
        _onCompleted = onCompleted;
    }

    public ReadOnlyMemory<byte> Frame { get; }
    public PacketDisposition? Disposition { get; private set; }

    public bool TryComplete(PacketDisposition disposition)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return false;
        Disposition = disposition;
        _onCompleted?.Invoke(Frame);
        return true;
    }

    public void Dispose() => TryComplete(PacketDisposition.Block);
}

public sealed class BoundedSetupQueue
{
    private readonly Queue<ReadOnlyMemory<byte>> _items = new();
    private readonly int _maxPackets;
    private readonly int _maxBytes;
    private int _bytes;

    public BoundedSetupQueue(int maxPackets, int maxBytes)
    {
        if (maxPackets <= 0) throw new ArgumentOutOfRangeException(nameof(maxPackets));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxPackets = maxPackets;
        _maxBytes = maxBytes;
    }

    public int Count => _items.Count;
    public int Bytes => _bytes;

    public bool TryEnqueue(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length > _maxBytes || _items.Count >= _maxPackets || _bytes > _maxBytes - frame.Length) return false;
        var copy = frame.ToArray();
        _items.Enqueue(copy);
        _bytes += copy.Length;
        return true;
    }

    public bool TryDequeue(out ReadOnlyMemory<byte> frame)
    {
        if (_items.Count == 0)
        {
            frame = default;
            return false;
        }

        frame = _items.Dequeue();
        _bytes -= frame.Length;
        return true;
    }
}
