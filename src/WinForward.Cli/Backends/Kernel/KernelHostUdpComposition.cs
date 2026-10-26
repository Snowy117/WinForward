using WinForward.Core;
using WinForward.Runtime;
using WinForward.Runtime.Socks5;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The collaborators the Kernel host UDP path borrows from <see cref="KernelBackend"/>: the same pool,
/// executor, address-cache, self-traffic and activity-clock instances the packet path composes the
/// shared engine from. The backend owns and disposes them; the host service and its coordinator only
/// borrow them. The rule-index target table is not here because it is produced by the policy compile
/// that startup runs after the composition exists.
/// </summary>
internal sealed record KernelHostUdpComposition(
    NativeBufferPool SetupQueuePool,
    NativeBufferPool ReceiveWindowPool,
    SetupExecutor SetupExecutor,
    Socks5AddressCache AddressCache,
    SelfTrafficRegistry SelfTraffic,
    ActivityBucketClock ActivityClock);
