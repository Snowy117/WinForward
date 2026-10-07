using System.Runtime.InteropServices;

namespace WinForward.E2E.Client.Lanes;

/// <summary>
/// One lane run's shape: the pace, the offer deadline, the bounded defer queue and the two buffers the
/// engine owns. Everything a lane decides about admission lives in the policy, not here.
/// </summary>
/// <remarks>
/// <see cref="RatePerSecond"/> of 0 paces nothing — every intended instant is the start — which is what
/// the allocation gate and the loop tests use to drive slots back to back without wall-clock waiting.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct LaneEngineOptions
{
    /// <summary>
    /// Declared because the grace bound below carries a default: a struct with a property initializer
    /// must declare a constructor, and moving the default onto every call site would spread "one second"
    /// across the arms. It is <see langword="public"/> because a parameterless struct constructor must
    /// be, and the type itself is <see langword="internal"/>, so nothing leaves the assembly.
    /// </summary>
    public LaneEngineOptions()
    {
    }

    /// <summary>Offered slots per second; the pace <see cref="Pacer"/> is built from.</summary>
    internal required double RatePerSecond { get; init; }

    /// <summary>The pacing epoch: the instant slot 0 was intended for.</summary>
    internal required long StartTicks { get; init; }

    /// <summary>The instant the offer loop stops offering; the last slot starts before it.</summary>
    internal required long DeadlineTicks { get; init; }

    /// <summary>
    /// How many deferred intents the queue holds before it drops them. A slot that arrives at a full
    /// queue is dropped rather than pushed in front of the older intents already waiting.
    /// </summary>
    internal required int BacklogLimit { get; init; }

    /// <summary>The buffer <see cref="ILanePolicy.BuildRequest"/> frames into, one per lane.</summary>
    internal required int SendBufferBytes { get; init; }

    /// <summary>The buffer <see cref="ILaneTransport.ReceiveAsync"/> reads into, one per lane.</summary>
    internal required int ReceiveBufferBytes { get; init; }

    /// <summary>
    /// The bound on the arm-end grace drain (D18.5 #4). Production uses one second, the bound
    /// <c>GraceDrainAsync</c> has always had; tests inject something shorter.
    /// </summary>
    internal long DrainLimitTicks { get; init; } = Clock.FromSeconds(1);
}
