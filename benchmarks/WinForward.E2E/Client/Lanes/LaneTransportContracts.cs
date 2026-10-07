using System.Runtime.InteropServices;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client.Lanes;

/// <summary>
/// What a lane transport observed on its socket, with no verdict about the wire format: the transport
/// reports the arrival, the policy decides what it means (D18.1). A stream transport has to find frame
/// boundaries to hand a message up at all, so it may report <see cref="Malformed"/> with the framing
/// reason it hit; it still does not judge whether the bytes were legal, it says which read failed.
/// </summary>
internal enum LaneReceiveKind
{
    /// <summary>
    /// A message arrived. <see cref="LaneReceiveResult.Length"/> bytes of the destination hold it —
    /// the datagram on a datagram lane, the framed message on a stream lane.
    /// </summary>
    Payload = 0,

    /// <summary>
    /// The peer closed its side. Terminal: the receive loop stops once the policy has seen it, exactly
    /// as a stream lane stopped at end of stream before.
    /// </summary>
    EndOfStream = 1,

    /// <summary>
    /// One message could not be read as a message. Not terminal — one bad message is not the lane, the
    /// same way one corrupt datagram never ended a UDP lane. The stream case that genuinely cannot
    /// continue (a desynchronized frame boundary) is an <see cref="IoError"/> instead, because no
    /// further message can be read from that stream.
    /// </summary>
    // ReSharper disable once UnusedMember.Global // One of the seam's four kinds: the engine only tests the two terminal ones, and the adapters that report this one land in 2a-3.
    Malformed = 2,

    /// <summary>
    /// The transport itself failed: a socket error, or a stream that can no longer be framed.
    /// Terminal: the receive loop stops once the policy has seen it.
    /// </summary>
    IoError = 3,
}

/// <summary>
/// One receive outcome. <see cref="Length"/> is how many bytes of the destination the transport filled
/// for this outcome and is 0 for the outcomes that carry none; <see cref="Detail"/> carries the
/// framing-level reason when the transport has one, and is only meaningful for
/// <see cref="LaneReceiveKind.Malformed"/> and <see cref="LaneReceiveKind.IoError"/>.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct LaneReceiveResult(LaneReceiveKind Kind, int Length, FrameDecodeError Detail = FrameDecodeError.None);

/// <summary>
/// The outcome of <see cref="ILaneTransport.OpenAsync"/>. Connect failure is a result, never an
/// exception (D18.1): a lane that cannot connect is an expected outcome — it offered none of its share
/// of the schedule, so the arm records a short schedule and keeps running.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct LaneOpenResult(bool Ok, string? Error);

/// <summary>
/// The outcome of one <see cref="ILaneTransport.SendAsync"/>. <see cref="WouldBlock"/> is the
/// transport's own pre-await observation of its socket send (audit §9.6, the one source of the
/// published <c>sendWouldBlock</c> counter); <see cref="Error"/> carries the failure text when
/// <see cref="Accepted"/> is false.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct LaneSendResult(bool Accepted, bool WouldBlock, string? Error);

/// <summary>
/// One lane's wire endpoint. The real adapters (a TCP stream and a UDP datagram lane) live beside this
/// contract; it reports what it received and never interprets whether the wire format was legal.
/// </summary>
/// <remarks>
/// The engine owns the loop, the transport owns the socket, and both failure channels are results
/// rather than exceptions: <see cref="OpenAsync"/> answers with <see cref="LaneOpenResult"/>,
/// <see cref="SendAsync"/> with <see cref="LaneSendResult"/> and <see cref="ReceiveAsync"/> with
/// <see cref="LaneReceiveResult"/>. A single bad send must not end a lane's schedule and a single bad
/// receive must not end its loop; a transport that throws instead of answering breaks that contract
/// and the arm fails loudly.
/// </remarks>
internal interface ILaneTransport : IDisposable
{
    /// <summary>
    /// Connects. Failure — a socket error, a cancellation, a disposed socket — is reported as
    /// <see cref="LaneOpenResult.Ok"/> false rather than thrown, because a lane that cannot connect is
    /// an outcome the arm records. The engine calls this once, on the calling thread, before either
    /// loop exists.
    /// </summary>
    // ReSharper disable once UnusedParameter.Global // The token is the lane's cancellation channel; an adapter that connects without it still has to accept it.
    ValueTask<LaneOpenResult> OpenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends one framed message. An implementation sets <see cref="LaneSendResult.WouldBlock"/> from
    /// its own socket send before it awaits it — the engine reads <c>IsCompleted</c> off the returned
    /// task before its single await (audit §9.6), and the two observations are the same property on
    /// either side of the seam.
    /// </summary>
    // ReSharper disable once UnusedParameter.Global // The token is the lane's cancellation channel; an adapter that sends without it still has to accept it.
    ValueTask<LaneSendResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>
    /// Receives one message into <paramref name="destination"/>. The buffer belongs to the engine and
    /// the bytes written into it do not outlive the call. A socket failure is reported as
    /// <see cref="LaneReceiveKind.IoError"/>, never thrown.
    /// </summary>
    ValueTask<LaneReceiveResult> ReceiveAsync(Memory<byte> destination, CancellationToken cancellationToken);
}
