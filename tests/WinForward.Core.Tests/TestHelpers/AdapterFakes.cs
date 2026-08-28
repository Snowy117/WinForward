using System.Net;
using WinForward.Core;
using WinForward.Windows;

namespace WinForward.Core.Tests;

/// <summary>
/// Returns a fixed adapter-local address (or null to model an addressless adapter) and counts
/// calls so forwarded-redirect tests can assert the resolver was consulted.
/// </summary>
internal sealed class FakeLocalAddressProvider(IPAddress? address = null) : IAdapterLocalAddressProvider
{
    public int Calls { get; private set; }

    public IPAddress? SelectLocalAddress(string adapterId, AddressFamilyKind family, IPAddress clientAddress)
    {
        Calls++;
        return address;
    }
}
