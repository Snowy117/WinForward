namespace WinForward.E2E.Contracts;

/// <summary>
/// The <c>metrics</c> object of a <c>latency</c> result record: one flat family per protocol, in write
/// order. Every value here contains a dot, so each constant is <em>one</em> member name that happens
/// to be dotted -- <see cref="Latency.TcpSent"/> declares the path <c>metrics/tcp.sent</c>, never an
/// object named <c>tcp</c>.
/// </summary>
/// <remarks>
/// <para><b>Conditional blocks.</b> The two families are published per protocol: an arm that ran only
/// UDP publishes no <c>tcp.*</c> key at all, and one that ran only TCP publishes no <c>udp.*</c> key.
/// The omission is always a whole block and only ever means "this arm did not run that protocol",
/// never "the value is unknown": inside a published block an unknown reading keeps its key and
/// publishes JSON <see langword="null"/>. The nullable readings are <see cref="Latency.TcpAchievedRate"/> and
/// <see cref="Latency.TcpMeanConnectMs"/> (null with no elapsed time and with no successful connect sample),
/// <see cref="Latency.UdpAchievedRate"/> (null with no elapsed time) and <see cref="Latency.UdpLossRate"/> (null when
/// nothing was sent, which is the same empty population).</para>
/// <para><b>Arrays.</b> <see cref="Latency.TcpLaneSupplied"/> and <see cref="Latency.TcpLaneSentOk"/> hold one
/// element per TCP lane in lane order, so their arity is the arm's lane count; the UDP side is a
/// single lane and publishes the scalar <see cref="Latency.UdpLaneStarted"/> instead.</para>
/// <para>The record's <c>gates</c> keys are shared and live in <see cref="ArmKeys.Common.Gates"/>; its
/// <c>parameters</c> keys in <see cref="ArmKeys.Common.Parameters"/>.</para>
/// </remarks>
public static partial class ArmKeys
{
    /// <summary>Keys under <c>metrics</c> for the <c>latency</c> kind.</summary>
    public static class Latency
    {
        /// <summary>0 or 1: whether the TCP lane body ran at all, summed across lanes.</summary>
        public const string TcpLaneStarted = "tcp.laneStarted";

        /// <summary>Requests offered per TCP lane, in lane order; arity is the lane count.</summary>
        public const string TcpLaneSupplied = "tcp.laneSupplied";

        /// <summary>Requests the kernel accepted per TCP lane, in lane order; arity is the lane count.</summary>
        public const string TcpLaneSentOk = "tcp.laneSentOk";

        /// <summary>Requests the TCP lanes offered.</summary>
        public const string TcpSupplied = "tcp.supplied";

        /// <summary>Requests the TCP lanes sent.</summary>
        public const string TcpSent = "tcp.sent";

        /// <summary>TCP sends the kernel did not accept synchronously; those sends still complete.</summary>
        public const string TcpSendWouldBlock = "tcp.sendWouldBlock";

        /// <summary>TCP requests offered at a full in-flight window and therefore deferred.</summary>
        public const string TcpWindowOverflow = "tcp.windowOverflow";

        /// <summary>Deferred TCP requests the bounded queue discarded.</summary>
        public const string TcpBacklogDrops = "tcp.backlogDrops";

        /// <summary>Individual TCP sends that threw; each costs one sample, not the schedule.</summary>
        public const string TcpSendFailures = "tcp.sendFailures";

        /// <summary>TCP requests sent but neither answered, failed nor dropped when the lane stopped.</summary>
        public const string TcpAbandonedAtTeardown = "tcp.abandonedAtTeardown";

        /// <summary>TCP requests that reached no histogram: supplied minus sent.</summary>
        public const string TcpClientSendLoss = "tcp.clientSendLoss";

        /// <summary>Valid TCP frames received, duplicates included.</summary>
        public const string TcpReceived = "tcp.received";

        /// <summary>TCP requests still unanswered, plus deferred requests that never got a slot.</summary>
        public const string TcpOutstandingAtTeardown = "tcp.outstandingAtTeardown";

        /// <summary>TCP frames that failed their checksum.</summary>
        public const string TcpCorrupt = "tcp.corrupt";

        /// <summary>TCP frames that decoded to something other than a valid frame, or a fatal socket error.</summary>
        public const string TcpProtocolErrors = "tcp.protocolErrors";

        /// <summary>TCP lanes whose peer closed the stream first.</summary>
        public const string TcpRemoteClosed = "tcp.remoteClosed";

        /// <summary>Valid TCP frames that matched no outstanding request.</summary>
        public const string TcpUnmatchedReplies = "tcp.unmatchedReplies";

        /// <summary>Ceiling on directly measurable TCP latency, in milliseconds; 0 when undefined.</summary>
        public const string TcpWindowCeilingMs = "tcp.windowCeilingMs";

        /// <summary>1 when a TCP lane never connected or its offer loop ended before its deadline.</summary>
        public const string TcpScheduleTruncated = "tcp.scheduleTruncated";

        /// <summary>Achieved TCP requests per second; null when no time passed.</summary>
        public const string TcpAchievedRate = "tcp.achievedRate";

        /// <summary>TCP connects attempted: the per-lane connects plus the 1 Hz probe.</summary>
        public const string TcpConnectAttempts = "tcp.connectAttempts";

        /// <summary>TCP connects in that same population that failed.</summary>
        public const string TcpConnectFailures = "tcp.connectFailures";

        /// <summary>Mean TCP connect duration in milliseconds; null when no connect succeeded.</summary>
        public const string TcpMeanConnectMs = "tcp.meanConnectMs";

        /// <summary>0 or 1: whether the UDP lane body ran at all.</summary>
        public const string UdpLaneStarted = "udp.laneStarted";

        /// <summary>Requests the UDP lane offered.</summary>
        public const string UdpSupplied = "udp.supplied";

        /// <summary>Requests the UDP lane sent.</summary>
        public const string UdpSent = "udp.sent";

        /// <summary>UDP sends the kernel did not accept synchronously; those sends still complete.</summary>
        public const string UdpSendWouldBlock = "udp.sendWouldBlock";

        /// <summary>UDP requests offered at a full in-flight window and therefore deferred.</summary>
        public const string UdpWindowOverflow = "udp.windowOverflow";

        /// <summary>Deferred UDP requests the bounded queue discarded.</summary>
        public const string UdpBacklogDrops = "udp.backlogDrops";

        /// <summary>Individual UDP sends that threw; each costs one sample, not the schedule.</summary>
        public const string UdpSendFailures = "udp.sendFailures";

        /// <summary>UDP requests sent but neither answered, failed nor dropped when the lane stopped.</summary>
        public const string UdpAbandonedAtTeardown = "udp.abandonedAtTeardown";

        /// <summary>UDP requests that reached no histogram: supplied minus sent.</summary>
        public const string UdpClientSendLoss = "udp.clientSendLoss";

        /// <summary>Valid UDP frames received, duplicates included.</summary>
        public const string UdpReceived = "udp.received";

        /// <summary>UDP frames that decoded to something other than a valid frame, or failed their filler.</summary>
        public const string UdpCorrupt = "udp.corrupt";

        /// <summary>UDP receive-loop socket errors.</summary>
        public const string UdpProtocolErrors = "udp.protocolErrors";

        /// <summary>Valid UDP frames that matched no outstanding request.</summary>
        public const string UdpUnmatchedReplies = "udp.unmatchedReplies";

        /// <summary>UDP replies bearing a connection id this socket never used.</summary>
        public const string UdpForeignConnection = "udp.foreignConnection";

        /// <summary>UDP requests still unanswered, plus deferred requests that never got a slot.</summary>
        public const string UdpOutstandingAtTeardown = "udp.outstandingAtTeardown";

        /// <summary>Ceiling on directly measurable UDP latency, in milliseconds; 0 when undefined.</summary>
        public const string UdpWindowCeilingMs = "udp.windowCeilingMs";

        /// <summary>1 when the UDP offer loop ended before its deadline.</summary>
        public const string UdpScheduleTruncated = "udp.scheduleTruncated";

        /// <summary>Sent minus matched arrivals over sent; null when nothing was sent.</summary>
        public const string UdpLossRate = "udp.lossRate";

        /// <summary>Achieved UDP requests per second; null when no time passed.</summary>
        public const string UdpAchievedRate = "udp.achievedRate";
    }
}
