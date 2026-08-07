using WinForward.NdisApi;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class NdisApiAbiTests
{
    [Fact]
    public void PinnedX64LayoutMatchesV362NonJumboHeader()
    {
        NdisApiAbi.AssertManagedX64Layout();
    }

    [Fact]
    public void IpHelperOwnerRowsMatchWindowsLayouts()
    {
        IpHelperAbi.AssertManagedLayout();
    }
}
