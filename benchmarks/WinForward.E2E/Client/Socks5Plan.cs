namespace WinForward.E2E.Client;

/// <summary>
/// The plan-side rule of <c>--socks5</c>: only arms whose traffic can travel over a SOCKS5 CONNECT are
/// accepted. The refusal happens before an output directory or a socket is created, because a plan
/// with a UDP or DNS arm cannot be measured through the proxy and sending it directly would measure a
/// path the row did not declare.
/// </summary>
internal static class Socks5Plan
{
    private const string TcpProtocol = "tcp";

    /// <summary>
    /// Whether every arm is TCP-only, or the first arm that is not and why. The accepted kinds are
    /// <c>idle</c> (no traffic), <c>reliability</c>, <c>throughput</c>, <c>persistent</c>, and
    /// <c>latency</c> when it declares <c>"tcp"</c>. <see langword="base"/> is refused because its loss phase is
    /// always UDP even when its latency phase declares <c>"tcp"</c>.
    /// </summary>
    internal static bool TryValidate(IReadOnlyList<ArmSpec> arms, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arms);

        foreach (var arm in arms)
        {
            if (arm.Kind is "idle" or "reliability" or "throughput" or "persistent")
            {
                continue;
            }

            if (string.Equals(arm.Kind, "latency", StringComparison.Ordinal))
            {
                if (string.Equals(arm.Protocol, TcpProtocol, StringComparison.Ordinal))
                {
                    continue;
                }

                error = $"--socks5 carries TCP CONNECT only; arm '{arm.Name}' declares protocol '{arm.Protocol}' and its UDP lane cannot go through the proxy";
                return false;
            }

            error = $"--socks5 carries TCP CONNECT only; arm '{arm.Name}' (kind '{arm.Kind}') would carry UDP or DNS, which CONNECT cannot transport; a SOCKS5 control plan must be TCP-only";
            return false;
        }

        error = null;
        return true;
    }
}
