namespace WinForward.Protocols;

/// <summary>
/// Diagnostics-only header-walk sinks for the per-packet path: <see cref="ParseWalk"/> counts
/// classifier parses (<see cref="IPTcpUdpPacket.TryParse"/>), <see cref="RevalidateWalk"/> counts
/// span-taking endpoint rewrites (which re-derive every header fact from the frame) and
/// <see cref="ViewRewrite"/> counts layout-driven rewrites (which consume the parse's proofs
/// instead). All three are null in production, and each site reads its sink once and
/// invokes it through null propagation — the <c>FlowTable.GateHoldProbe</c> shape.
/// <para>
/// The sinks are <c>[ThreadStatic]</c> because the test host runs xUnit collections in parallel:
/// a process-wide counter on a shared parser would be inflated by sibling parser tests, while a
/// thread-scoped one is driven, asserted and thread-id-checked on a single thread.
/// </para>
/// </summary>
internal static class PacketPathProbe
{
    [ThreadStatic]
#pragma warning disable IDE1006 // The t_ prefix is the team convention for [ThreadStatic] fields (quality-guidelines.md): the editorconfig naming rules cannot match attributes, so the s_ rule would rename this field away from its thread-local marker.
    private static Action? t_parseWalk;
#pragma warning restore IDE1006

    [ThreadStatic]
#pragma warning disable IDE1006 // The t_ prefix is the team convention for [ThreadStatic] fields (quality-guidelines.md): the editorconfig naming rules cannot match attributes, so the s_ rule would rename this field away from its thread-local marker.
    private static Action? t_revalidateWalk;
#pragma warning restore IDE1006

    [ThreadStatic]
#pragma warning disable IDE1006 // The t_ prefix is the team convention for [ThreadStatic] fields (quality-guidelines.md): the editorconfig naming rules cannot match attributes, so the s_ rule would rename this field away from its thread-local marker.
    private static Action? t_viewRewrite;
#pragma warning restore IDE1006

    // ReSharper disable ConvertToAutoProperty -- [ThreadStatic] can only be applied to a static field, so
    // these three probes must keep their thread-local backing fields rather than becoming auto-properties.
    /// <summary>Fires once per <see cref="IPTcpUdpPacket.TryParse"/> call on the calling thread.</summary>
    internal static Action? ParseWalk
    {
        get => t_parseWalk;
        set => t_parseWalk = value;
    }

    /// <summary>Fires once per span-taking TCP endpoint rewrite on the calling thread.</summary>
    internal static Action? RevalidateWalk
    {
        get => t_revalidateWalk;
        set => t_revalidateWalk = value;
    }

    /// <summary>Fires once per layout-taking TCP endpoint rewrite on the calling thread.</summary>
    internal static Action? ViewRewrite
    {
        get => t_viewRewrite;
        set => t_viewRewrite = value;
    }
    // ReSharper restore ConvertToAutoProperty
}
