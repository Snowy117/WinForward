using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WinForward.Controller;

/// <summary>
/// 端到端测试的客户端：真实进程、真实 TCP 连接，发 N 字节伪随机数据并校验回显。
/// 目标写的是「原始目的地」，被驱动重定向后由控制器中继，客户端不感知。
/// </summary>
internal static class ClientCommand
{
    private const int ChunkSize = 64 * 1024;

    internal static int Execute(string[] args)
    {
        CommandLine line = CommandLine.Parse(args);
        string target = line.Require("target");
        long bytes = line.GetLong("bytes", 1024 * 1024);
        ulong seed = (ulong)line.GetLong("seed", 1);
        int requestedFamily = line.GetInt("family", 0);
        int timeoutMs = line.GetInt("timeout-ms", 30000);

        if (bytes <= 0)
        {
            throw new ArgumentException("--bytes 必须大于 0");
        }

        IPEndPoint endPoint;
        try
        {
            endPoint = EndPointParser.Parse(target);
        }
        catch (Exception exception) when (exception is ArgumentException or SocketException)
        {
            Console.Error.WriteLine($"client FAIL target={target} {exception.GetType().Name}: {exception.Message}");
            return 1;
        }

        int actualFamily = endPoint.AddressFamily == AddressFamily.InterNetworkV6 ? 6 : 4;
        if (requestedFamily != 0 && requestedFamily != actualFamily)
        {
            Console.Error.WriteLine($"client FAIL target={target} 的地址族是 IPv{actualFamily}，与 --family {requestedFamily} 不符");
            return 1;
        }

        using Socket socket = new(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        if (endPoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = false;
        }

        Console.WriteLine($"client target={target} family={actualFamily} bytes={bytes} seed={seed}");

        using CancellationTokenSource timeout = new(timeoutMs);
        try
        {
            socket.Connect(endPoint);
            Task<(long Bytes, string Sha256)> send = Task.Run(() => SendAsync(socket, bytes, seed, timeout.Token));
            Task<(long Bytes, string Sha256)> receive = Task.Run(() => ReceiveAsync(socket, bytes, timeout.Token));
            Task.WaitAll(send, receive);

            (long sentBytes, string sentHash) = send.Result;
            (long receivedBytes, string receivedHash) = receive.Result;

            Console.WriteLine($"client sent={sentBytes} received={receivedBytes}");
            Console.WriteLine($"client sha256 sent={sentHash} received={receivedHash}");

            if (sentBytes != bytes || receivedBytes != bytes)
            {
                Console.Error.WriteLine($"client FAIL 字节数不一致：发送 {sentBytes}，接收 {receivedBytes}，期望 {bytes}");
                return 1;
            }

            if (!string.Equals(sentHash, receivedHash, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("client FAIL 校验和不一致");
                return 1;
            }

            Console.WriteLine($"client OK bytes={bytes.ToString(CultureInfo.InvariantCulture)} sha256={sentHash}");
            return 0;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                               or AggregateException or IOException)
        {
            Console.Error.WriteLine($"client FAIL target={target} {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task<(long Bytes, string Sha256)> SendAsync(Socket socket, long bytes, ulong seed, CancellationToken token)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        XorShift64 random = new(seed);
        byte[] buffer = new byte[ChunkSize];
        long remaining = bytes;
        long sent = 0;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(ChunkSize, remaining);
            random.Fill(buffer.AsSpan(0, chunk));
            hash.AppendData(buffer, 0, chunk);

            int offset = 0;
            while (offset < chunk)
            {
                offset += await socket.SendAsync(buffer.AsMemory(offset, chunk - offset), SocketFlags.None, token);
            }

            remaining -= chunk;
            sent += chunk;
        }

        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // 对端可能已经先关了。
        }

        return (sent, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static async Task<(long Bytes, string Sha256)> ReceiveAsync(Socket socket, long bytes, CancellationToken token)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[ChunkSize];
        long received = 0;
        while (received < bytes)
        {
            int read = await socket.ReceiveAsync(buffer.AsMemory(0, (int)Math.Min(ChunkSize, bytes - received)),
                                                 SocketFlags.None, token);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            received += read;
        }

        return (received, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private sealed class XorShift64
    {
        private ulong _state;

        internal XorShift64(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

        internal void Fill(Span<byte> buffer)
        {
            int index = 0;
            while (index < buffer.Length)
            {
                _state ^= _state << 13;
                _state ^= _state >> 7;
                _state ^= _state << 17;
                int count = Math.Min(sizeof(ulong), buffer.Length - index);
                for (int byteIndex = 0; byteIndex < count; byteIndex++)
                {
                    buffer[index + byteIndex] = (byte)(_state >> (8 * byteIndex));
                }

                index += count;
            }
        }
    }
}
