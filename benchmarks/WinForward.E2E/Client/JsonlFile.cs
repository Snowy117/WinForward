using System.Text.Json;

namespace WinForward.E2E.Client;

internal sealed class JsonlFile : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MemoryStream _buffer = new(4096);
    private readonly Utf8JsonWriter _writer;
    private bool _disposed;

    internal JsonlFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.None);
        _writer = new Utf8JsonWriter(_buffer);
    }

    internal async ValueTask WriteAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _buffer.SetLength(0);
            _writer.Reset(_buffer);
            write(_writer);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            _buffer.WriteByte((byte)'\n');
            await _stream.WriteAsync(_buffer.GetBuffer().AsMemory(0, (int)_buffer.Length), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
            await _buffer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
