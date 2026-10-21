using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinForward.Userland.Configuration;
using WinForward.Userland.Relay;

namespace WinForward.Userland.Tests;

/// <summary>
/// SOCKS5 客户端：三种地址形态的线格式，以及每一条失败路径都必须给出可诊断的异常。
/// </summary>
/// <remarks>
/// 用 loopback 上的假上游（<see cref="Socks5FakeUpstream"/>），不碰网络也不碰驱动。
/// 每条用例守的是一个具体的检查：删掉它，对应用例必须变红（见 tools/userland/verify-gates.sh）。
/// </remarks>
internal static class Socks5Tests
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    internal static void Register()
    {
        TestRunner.Add("socks5/IPv4 目标：请求是 05 01 00 01 + 4 字节地址 + 端口", Ipv4Request);
        TestRunner.Add("socks5/IPv6 目标：请求是 05 01 00 04 + 16 字节地址 + 端口", Ipv6Request);
        TestRunner.Add("socks5/域名目标：请求是 05 01 00 03 + 长度 + ASCII + 端口", DomainRequest);
        TestRunner.Add("socks5/rep 非 0 抛 Socks5Exception 并带 rep 码与目标", RepIsRejected);
        TestRunner.Add("socks5/上游不可达时立即抛 Socks5Exception", UnreachableUpstream);
        TestRunner.Add("socks5/上游回复 0xFF 表示拒绝 no-auth", NoAuthRejected);
        TestRunner.Add("socks5/上游不回话时超时报错而不是挂住", HandshakeTimeoutIsReported);
        TestRunner.Add("socks5/上游回复版本不对时报错", WrongReplyVersion);
        TestRunner.Add("socks5/非 ASCII 域名在碰网络之前被拒绝", NonAsciiDomain);
        TestRunner.Add("socks5/没有上游时直连可用", DirectStillWorks);
        TestRunner.Add("socks5/上游失败时不回退直连", NoFallbackToDirect);
        TestRunner.Add("socks5/经上游 CONNECT 后数据面双向可通", DataFlowsThroughProxy);
    }

    private static void Ipv4Request()
    {
        bool preparedBeforeConnect = false;
        RunWithFake(new Socks5FakeOptions(), fake =>
        {
            using Socket socket = Connect(fake.Proxy,
                                          new IPEndPoint(IPAddress.Parse("192.168.100.4"), 40001),
                                          prepared => preparedBeforeConnect = !prepared.Connected);
            fake.WaitForRequest();
            Check.BytesEqual([0x05, 0x01, 0x00], fake.Greeting, "方法协商");
            Check.BytesEqual([0x05, 0x01, 0x00, 0x01, 0xC0, 0xA8, 0x64, 0x04, 0x9C, 0x41],
                             fake.Request, "IPv4 CONNECT 请求");
            Check.True(preparedBeforeConnect, "prepareSocket 必须在 connect 之前调用（挂 redirect records 依赖这个顺序）");
        });
    }

    private static void Ipv6Request()
    {
        RunWithFake(new Socks5FakeOptions(), fake =>
        {
            using Socket socket = Connect(fake.Proxy, new IPEndPoint(IPAddress.Parse("::1"), 8080));
            fake.WaitForRequest();
            byte[] expected = new byte[22];
            expected[0] = 0x05;
            expected[1] = 0x01;
            expected[3] = 0x04;
            expected[^3] = 0x01;
            expected[^2] = 0x1F;
            expected[^1] = 0x90;
            Check.BytesEqual(expected, fake.Request, "IPv6 CONNECT 请求");
        });
    }

    private static void DomainRequest()
    {
        RunWithFake(new Socks5FakeOptions(), fake =>
        {
            using Socket socket = Connect(fake.Proxy, new DnsEndPoint("example.com", 443));
            fake.WaitForRequest();
            byte[] expected = [0x05, 0x01, 0x00, 0x03, 0x0B, .. Encoding.ASCII.GetBytes("example.com"), 0x01, 0xBB];
            Check.BytesEqual(expected, fake.Request, "域名 CONNECT 请求");
        });
    }

    private static void RepIsRejected()
    {
        RunWithFake(new Socks5FakeOptions { Rep = 0x05 }, fake =>
        {
            Socks5Exception? thrown = ThrowsSocks5(() => Connect(fake.Proxy,
                                                                 new IPEndPoint(IPAddress.Parse("192.168.100.4"), 40001)));
            Check.True(thrown is not null, "rep=5 必须抛 Socks5Exception");
            Check.Equal((byte?)0x05, thrown!.Rep, "异常里带 rep 码");
            Check.True(thrown.Message.Contains("192.168.100.4:40001", StringComparison.Ordinal),
                       $"异常消息要带目标，实际：{thrown.Message}");
            Check.True(thrown.Message.Contains("连接被拒", StringComparison.Ordinal),
                       $"异常消息要带 rep 的含义，实际：{thrown.Message}");
        });
    }

    private static void UnreachableUpstream()
    {
        Stopwatch watch = Stopwatch.StartNew();
        Socks5Exception? thrown = ThrowsSocks5(() => Connect(new Socks5Endpoint("127.0.0.1", FreeTcpPort()),
                                                             new IPEndPoint(IPAddress.Loopback, 80)));
        watch.Stop();
        Check.True(thrown is not null, "连不上上游必须抛 Socks5Exception，不能静默");
        Check.True(thrown!.Message.Contains("SOCKS5 上游", StringComparison.Ordinal),
                   $"异常消息要指出是上游连不上，实际：{thrown.Message}");
        Check.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"连不上时不能挂住，实际 {watch.Elapsed.TotalSeconds:0.###} 秒");
    }

    private static void NoAuthRejected()
    {
        RunWithFake(new Socks5FakeOptions { MethodReply = 0xFF }, fake =>
        {
            Socks5Exception? thrown = ThrowsSocks5(() => Connect(fake.Proxy, new IPEndPoint(IPAddress.Loopback, 80)));
            Check.True(thrown is not null, "上游只提供认证方法时必须抛 Socks5Exception");
            Check.True(thrown!.Message.Contains("0xFF", StringComparison.Ordinal),
                       $"异常消息要带 0xFF，实际：{thrown.Message}");
        });
    }

    private static void HandshakeTimeoutIsReported()
    {
        RunWithFake(new Socks5FakeOptions { ReplyConnect = false }, fake =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            Socks5Exception? thrown = ThrowsSocks5(() => Connect(fake.Proxy,
                                                                 new IPEndPoint(IPAddress.Loopback, 80),
                                                                 timeout: TimeSpan.FromMilliseconds(300)));
            watch.Stop();
            Check.True(thrown is not null, "上游不回话时必须抛 Socks5Exception");
            Check.True(thrown!.Message.Contains("秒内没有完成 CONNECT", StringComparison.Ordinal),
                       $"异常消息要说明超时，实际：{thrown.Message}");
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"超时预算必须生效，实际 {watch.Elapsed.TotalSeconds:0.###} 秒");
        });
    }

    private static void WrongReplyVersion()
    {
        RunWithFake(new Socks5FakeOptions { ReplyVersion = 0x04 }, fake =>
        {
            Socks5Exception? thrown = ThrowsSocks5(() => Connect(fake.Proxy, new IPEndPoint(IPAddress.Loopback, 80)));
            Check.True(thrown is not null, "回复版本不是 5 时必须抛 Socks5Exception");
            Check.True(thrown!.Message.Contains("版本", StringComparison.Ordinal),
                       $"异常消息要指出是版本问题，实际：{thrown.Message}");
        });
    }

    private static void NonAsciiDomain()
    {
        Socks5Exception? thrown = ThrowsSocks5(() => Connect(new Socks5Endpoint("127.0.0.1", FreeTcpPort()),
                                                             new DnsEndPoint("例子.com", 80)));
        Check.True(thrown is not null, "SOCKS5 域名只接受 ASCII，非 ASCII 目标必须报错");
        Check.True(thrown!.Message.Contains("ASCII", StringComparison.Ordinal),
                   $"异常消息要指出 ASCII 限制（说明编码校验发生在网络之前），实际：{thrown.Message}");
    }

    private static void DirectStillWorks()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            Task serving = Task.Run(() => EchoOnce(listener));
            using Socket socket = new OutboundConnector(null)
                .ConnectAsync((IPEndPoint)listener.LocalEndpoint, [], HandshakeTimeout, CancellationToken.None)
                .GetAwaiter().GetResult();
            socket.Send(Encoding.ASCII.GetBytes("ping"));
            Check.Equal("ping", Encoding.ASCII.GetString(ReceiveExactly(socket, 4)), "没有上游时直连的往返数据");
            socket.Shutdown(SocketShutdown.Both);
            Check.True(serving.Wait(TimeSpan.FromSeconds(5)), "直连会话应当正常收尾");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void NoFallbackToDirect()
    {
        TcpListener destination = new(IPAddress.Loopback, 0);
        destination.Start();
        try
        {
            OutboundConnector connector = new(new Socks5Endpoint("127.0.0.1", FreeTcpPort()));
            Socks5Exception? thrown = ThrowsSocks5(() => connector
                .ConnectAsync((IPEndPoint)destination.LocalEndpoint, [], HandshakeTimeout, CancellationToken.None)
                .GetAwaiter().GetResult());
            Check.True(thrown is not null, "上游连不上时必须失败，不能悄悄直连");
            Check.True(!destination.Pending(), "失败路径不得在目的地上建立连接（静默回退直连会在这里露馅）");
        }
        finally
        {
            destination.Stop();
        }
    }

    private static void DataFlowsThroughProxy()
    {
        TcpListener echo = new(IPAddress.Loopback, 0);
        echo.Start();
        try
        {
            Task serving = Task.Run(() => EchoOnce(echo));
            RunWithFake(new Socks5FakeOptions { Forward = true }, fake =>
            {
                OutboundConnector connector = new(fake.Proxy);
                using Socket socket = connector
                    .ConnectAsync((IPEndPoint)echo.LocalEndpoint, [], HandshakeTimeout, CancellationToken.None)
                    .GetAwaiter().GetResult();
                socket.Send(Encoding.ASCII.GetBytes("ping"));
                Check.Equal("ping", Encoding.ASCII.GetString(ReceiveExactly(socket, 4)), "经上游 CONNECT 后的往返数据");
                Check.Equal(1, fake.Connects, "假上游完成的 CONNECT 次数");
                fake.WaitForRequest();
                Check.Equal((byte)0x01, fake.Request[3], "请求里的地址类型");
                socket.Shutdown(SocketShutdown.Both);
            });
            Check.True(serving.Wait(TimeSpan.FromSeconds(5)), "经上游的会话应当正常收尾");
        }
        finally
        {
            echo.Stop();
        }
    }

    private static Socket Connect(Socks5Endpoint proxy,
                                  EndPoint destination,
                                  Action<Socket>? prepare = null,
                                  TimeSpan? timeout = null) =>
        Socks5Client.ConnectAsync(proxy, destination, prepare, timeout ?? HandshakeTimeout, CancellationToken.None)
                    .GetAwaiter().GetResult();

    private static Socks5Exception? ThrowsSocks5(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Socks5Exception exception)
        {
            return exception;
        }
    }

    private static void RunWithFake(Socks5FakeOptions options, Action<Socks5FakeUpstream> body)
    {
        Socks5FakeUpstream fake = Socks5FakeUpstream.Start(options);
        try
        {
            body(fake);
        }
        finally
        {
            fake.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (fake.Error.Length > 0)
        {
            throw new TestFailure($"假上游剧本失败：{fake.Error}");
        }
    }

    private static int FreeTcpPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static void EchoOnce(TcpListener listener)
    {
        using Socket socket = listener.AcceptSocket();
        socket.ReceiveTimeout = 5000;
        byte[] buffer = new byte[1024];
        while (true)
        {
            int read = socket.Receive(buffer);
            if (read == 0)
            {
                return;
            }

            socket.Send(buffer, 0, read, SocketFlags.None);
        }
    }

    private static byte[] ReceiveExactly(Socket socket, int count)
    {
        socket.ReceiveTimeout = 5000;
        byte[] data = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = socket.Receive(data, offset, count - offset, SocketFlags.None);
            if (read == 0)
            {
                throw new TestFailure($"只读到 {offset}/{count} 字节就 EOF");
            }

            offset += read;
        }

        return data;
    }
}
