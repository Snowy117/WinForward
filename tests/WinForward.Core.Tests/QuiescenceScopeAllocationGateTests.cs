using WinForward.Runtime;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class QuiescenceScopeAllocationGateTests
{
    [Fact]
    public void WarmEnterExitPairAllocatesNoManagedBytes()
    {
        var scope = new QuiescenceScope();

        for (var warm = 0; warm < 8; warm++)
        {
            if (!scope.TryEnter(out var warmLease)) Assert.Fail("the warm enter was refused");
            warmLease.Dispose();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int count = 4096;
        for (var index = 0; index < count; index++)
        {
            if (!scope.TryEnter(out var lease)) Assert.Fail("the measured enter was refused");
            lease.Dispose();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
