using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class DifferentialConnections
{
    internal const string SvchostPath = TestAppId.SvchostDevice;
    internal const string AppPath = TestAppId.AppDevice;
    internal const string OtherPath = TestAppId.OtherDevice;
    internal const string UnicodePath = TestAppId.UnicodeDeviceUpper;

    internal static IReadOnlyList<DifferentialConnection> All { get; } = new DifferentialConnection[]
    {
        new("ipv4 10.1.2.3:443 svchost", "0a010203", 443, 6, RuleAddressFamily.Ipv4, SvchostPath),
        new("ipv4 10.1.2.3:80 app", "0a010203", 80, 6, RuleAddressFamily.Ipv4, AppPath),
        new("ipv4 192.168.1.5:8080 svchost", "c0a80105", 8080, 6, RuleAddressFamily.Ipv4, SvchostPath),
        new("ipv6 2001:db8::1:443 svchost", "20010db8000000000000000000000001", 443, 6, RuleAddressFamily.Ipv6, SvchostPath),
        new("ipv6 2001:db8::2:80 app", "20010db8000000000000000000000002", 80, 6, RuleAddressFamily.Ipv6, AppPath),
        new("ipv4 10.1.2.3:1 other", "0a010203", 1, 6, RuleAddressFamily.Ipv4, OtherPath),
        new("ipv4 10.1.2.3:65535 other", "0a010203", 65535, 6, RuleAddressFamily.Ipv4, OtherPath),
        new("ipv4 10.1.2.3:443 大写路径", "0a010203", 443, 6, RuleAddressFamily.Ipv4, TestAppId.SvchostDeviceUpper),
        new("ipv4 10.1.2.3:443 后缀多一段", "0a010203", 443, 6, RuleAddressFamily.Ipv4, TestAppId.SvchostBakDevice),
        new("ipv4 10.1.2.3:443 无进程路径", "0a010203", 443, 6, RuleAddressFamily.Ipv4, null),
        new("ipv6 2001:db8:1::9:8443 other", "20010db8000100000000000000000009", 8443, 6, RuleAddressFamily.Ipv6, OtherPath),
        new("ipv4 172.16.0.9:53 other", "ac100009", 53, 6, RuleAddressFamily.Ipv4, OtherPath),
        new("ipv4 10.1.2.3:443 udp svchost", "0a010203", 443, 17, RuleAddressFamily.Ipv4, SvchostPath),
        new("ipv4 10.1.2.4:443 other", "0a010204", 443, 6, RuleAddressFamily.Ipv4, OtherPath),
        new("ipv4 203.0.113.9:9000 中文路径", "cb007109", 9000, 6, RuleAddressFamily.Ipv4, UnicodePath),
    };
}
