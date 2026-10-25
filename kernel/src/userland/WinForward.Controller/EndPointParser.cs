using System.Net;

namespace WinForward.Controller;

internal static class EndPointParser
{
    internal static IPEndPoint Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        int separator;
        string host;
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            int closing = text.IndexOf(']');
            if (closing < 0 || closing + 1 >= text.Length || text[closing + 1] != ':')
            {
                throw new ArgumentException($"IPv6 端点应写成 [地址]:端口：{text}");
            }

            host = text[1..closing];
            separator = closing + 1;
        }
        else
        {
            separator = text.LastIndexOf(':');
            if (separator <= 0)
            {
                throw new ArgumentException($"端点应写成 host:port：{text}");
            }

            host = text[..separator];
        }

        if (!ushort.TryParse(text[(separator + 1)..], System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out ushort port) || port == 0)
        {
            throw new ArgumentException($"端口不是 1..65535：{text}");
        }

        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            return new IPEndPoint(address, port);
        }

        IPAddress[] addresses = Dns.GetHostAddresses(host);
        if (addresses.Length == 0)
        {
            throw new ArgumentException($"解析不到主机名：{host}");
        }

        return new IPEndPoint(addresses[0], port);
    }
}
