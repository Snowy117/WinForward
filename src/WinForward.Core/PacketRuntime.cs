namespace WinForward.Core;

public enum PacketDisposition
{
    Pass,
    Block,
    ProxyConsumed
}

public sealed class PacketLease : IDisposable
{
    private int _completed;

    public PacketLease(ReadOnlyMemory<byte> frame) => Frame = frame;

    public ReadOnlyMemory<byte> Frame { get; }
    public PacketDisposition? Disposition { get; private set; }

    public bool TryComplete(PacketDisposition disposition)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return false;
        Disposition = disposition;
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
