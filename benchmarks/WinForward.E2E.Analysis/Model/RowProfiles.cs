namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// One measured row's declaration: the product, the configuration it ran, the arms its plan runs, and
/// where its port-53 and general UDP traffic goes.
/// </summary>
/// <param name="Product">The product under test, as the campaign's own plan names it.</param>
/// <param name="Config">The configuration the row ran, in one sentence.</param>
/// <param name="Plan">The key into <see cref="RowProfiles.PlanArms"/>.</param>
/// <param name="Dual">Whether the row runs a dual phase (a proxied and a direct lane).</param>
/// <param name="Udp53">One of the <c>Udp53*</c> carriage names.</param>
/// <param name="Udp">One of the <c>Udp*</c> carriage names.</param>
/// <remarks>
/// <b>This table is the analysis's own, not the campaign's.</b> It is what makes a row's absence of a
/// number a design statement rather than an unknown: §15 reads
/// <see cref="RowProfiles.PlanArms"/> to tell "declared but absent" from "present but undeclared", and
/// the UDP/DNS tables read <see cref="Udp"/> and <see cref="Udp53"/> to label a path instead of
/// printing a number that measures nothing.
/// </remarks>
internal sealed record RowProfile(
    string Product,
    string Config,
    string Plan,
    bool Dual,
    string Udp53,
    string Udp);

/// <summary>
/// The nine rows the campaign is declared to have, keyed by row id, plus the plan and ordering tables
/// every section reads.
/// </summary>
/// <remarks>
/// <para><b>Declared, never discovered.</b> A row's id would otherwise be whatever a directory is
/// called; here each id carries what the campaign meant by it, so a row that stopped running an arm
/// is reported as a gap (15) instead of quietly losing a column.</para>
/// <para><b>The order is output.</b> <see cref="Order"/> is the order rows are introduced in, and
/// <c>verdict.json</c>'s <c>rows</c> is sorted by it before anything else; a row outside it sorts
/// after every declared one, by <see cref="NaturalKey"/>.</para>
/// </remarks>
internal static class RowProfiles
{
    /// <summary>The control block that runs before the product block.</summary>
    internal const string ControlPre = "control-pre";

    /// <summary>The control block that runs after the product block.</summary>
    internal const string ControlPost = "control-post";

    /// <summary>Whether a row id is the block that runs before the product block.</summary>
    internal static bool IsControlPre(string rowId) => string.Equals(rowId, ControlPre, StringComparison.Ordinal);

    /// <summary>Whether a row id is the block that runs after the product block.</summary>
    internal static bool IsControlPost(string rowId) => string.Equals(rowId, ControlPost, StringComparison.Ordinal);

    /// <summary>The arms a plan runs; <c>full</c> is the whole measured set.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> PlanArms { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["full"] = ["IDLE", "LAT", "LATLOAD", "DNS", "DNSALT", "LOSS", "REL", "THRU", "MIX", "PERSIST"],
            ["udp only"] = ["LAT", "LATLOAD", "LOSS"],
            ["dns only"] = ["DNS", "DNSALT"],
            ["BASE only"] = ["BASE"],
        };

    /// <summary>
    /// The rows in the order they are introduced. A row not named here sorts after every declared row,
    /// so a tree with an extra row still produces a stable document.
    /// </summary>
    internal static IReadOnlyList<string> Order { get; } =
    [
        "control-pre",
        "wf-aot-opt",
        "wf-fdd-opt",
        "wf-aot-nativeudp",
        "wf-aot-dnsrelay",
        "proxifyre",
        "proxifier",
        "proxybridge",
        "control-post",
    ];

    /// <summary>The carriage name of a port-53 path that goes through the proxy.</summary>
    internal const string Udp53Relayed = "relayed";

    /// <summary>The carriage name of a port-53 path that reaches a local DNS target directly.</summary>
    internal const string Udp53LocalTarget = "direct-local-target";

    /// <summary>The carriage name of a port-53 path a product lets past by hardcoded rule.</summary>
    internal const string Udp53Hardcoded = "direct-hardcoded-bypass";

    /// <summary>The carriage name of a port-53 path a product cannot carry at all.</summary>
    private const string Udp53NotCarried = "direct-not-carried";

    /// <summary>The carriage name of a row that loads no product.</summary>
    internal const string Udp53NotApplicable = "n/a";

    /// <summary>The carriage name of general UDP carried inside TCP.</summary>
    internal const string UdpProxiedUtcp = "proxied-utcp";

    /// <summary>The carriage name of general UDP carried by a native relay.</summary>
    internal const string UdpProxiedNative = "proxied-native";

    /// <summary>The carriage name of general UDP a product does not carry.</summary>
    internal const string UdpNotCarried = "not-carried";

    /// <summary>The carriage name of general UDP on a row that loads no product.</summary>
    private const string UdpNotApplicable = "n/a";

    /// <summary>The cell every UDP number of a row whose product cannot carry UDP reads.</summary>
    internal const string NotCarriedCell = "not carried (UDP bypassed)";

    /// <summary>What each port-53 carriage means, in the reference's words.</summary>
    private static readonly Dictionary<string, string> s_udp53Labels = new(StringComparer.Ordinal)
    {
        [Udp53Relayed] = "relayed through the proxy",
        [Udp53LocalTarget] = "direct via the local DNS target (udp/53 forwarded verbatim, bypasses the proxy)",
        [Udp53Hardcoded] = "direct via the product's hardcoded port-53 pass-through",
        [Udp53NotCarried] = "direct (the product cannot carry UDP at all)",
        [Udp53NotApplicable] = "n/a (no product loaded: the direct path is the only path)",
    };

    /// <summary>What each general-UDP carriage means, in the reference's words.</summary>
    private static readonly Dictionary<string, string> s_udpLabels = new(StringComparer.Ordinal)
    {
        [UdpProxiedUtcp] = "proxied (UDP-over-TCP v2)",
        [UdpProxiedNative] = "proxied (native UDP relay)",
        [UdpNotCarried] = "not carried (UDP bypassed)",
        [UdpNotApplicable] = "n/a (no product loaded)",
    };

    /// <summary>
    /// The rows that leave every UDP datagram on the direct path, derived from the table rather than
    /// hand-maintained: a product that cannot carry UDP at all is exempt from the UDP gate.
    /// </summary>
    internal static IReadOnlySet<string> UdpIncapableRows => field ??=
        Profiles.Where(entry => string.Equals(entry.Value.Udp, UdpNotCarried, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The declared profiles, in the table's own order.</summary>
    internal static IReadOnlyDictionary<string, RowProfile> Declared => Profiles;

    /// <summary>The label of one port-53 carriage.</summary>
    internal static string Udp53Label(string carriage) => s_udp53Labels[carriage];

    /// <summary>The label of one general-UDP carriage.</summary>
    internal static string UdpLabel(string carriage) => s_udpLabels[carriage];

    private static IReadOnlyDictionary<string, RowProfile> Profiles { get; } =
        new Dictionary<string, RowProfile>(StringComparer.Ordinal)
        {
            ["wf-aot-opt"] = new(
                "WinForward AOT",
                "UDP-over-TCP v2 on, DNS local target on (reference configuration)",
                "full",
                Dual: true,
                Udp53LocalTarget,
                UdpProxiedUtcp),
            ["wf-fdd-opt"] = new(
                "WinForward framework-dependent",
                "same configuration as wf-aot-opt",
                "full",
                Dual: true,
                Udp53LocalTarget,
                UdpProxiedUtcp),
            ["wf-aot-nativeudp"] = new(
                "WinForward AOT",
                "UDP-over-TCP off, everything else identical to wf-aot-opt",
                "udp only",
                Dual: false,
                Udp53LocalTarget,
                UdpProxiedNative),
            ["wf-aot-dnsrelay"] = new(
                "WinForward AOT",
                "DNS local target off, everything else identical to wf-aot-opt",
                "dns only",
                Dual: false,
                Udp53Relayed,
                UdpProxiedUtcp),
            ["proxifyre"] = new(
                "ProxiFyre 2.6.1",
                "SOCKS5 rule for the client, supportedProtocols TCP+UDP",
                "full",
                Dual: true,
                Udp53Hardcoded,
                UdpProxiedNative),
            ["proxifier"] = new(
                "Proxifier 4.14",
                "SOCKS5 profile, process rules (no UDP support in the product)",
                "full",
                Dual: true,
                Udp53NotCarried,
                UdpNotCarried),
            ["proxybridge"] = new(
                "ProxyBridge 4.0.0",
                "PROXY rule for the client, DIRECT rule for sing-box",
                "full",
                Dual: true,
                Udp53Relayed,
                UdpProxiedNative),
            ["control-pre"] = new(
                "none (control block before the product block)",
                "no product loaded",
                "BASE only",
                Dual: false,
                Udp53NotApplicable,
                UdpNotApplicable),
            ["control-post"] = new(
                "none (control block after the product block)",
                "no product loaded",
                "BASE only",
                Dual: false,
                Udp53NotApplicable,
                UdpNotApplicable),
        };

    /// <summary>The row's declaration, or null for an id the table does not know.</summary>
    internal static RowProfile? Find(string rowId)
    {
        ArgumentNullException.ThrowIfNull(rowId);

        return Profiles.GetValueOrDefault(rowId);
    }

    /// <summary>The arms the row's plan runs, or null when the row itself is undeclared.</summary>
    internal static IReadOnlyList<string>? Planned(string rowId)
    {
        var profile = Find(rowId);
        return profile is null ? null : PlanArms[profile.Plan];
    }
}
