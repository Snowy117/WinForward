namespace WinForward.E2E.Client.Arms;

internal sealed class MixCounters
{
    internal MixCounters(int desktops)
    {
        PageConnectionsPerDesktop = new long[desktops];
        BulkFramesPerDesktop = new long[desktops];
        DnsSentPerDesktop = new long[desktops];
    }

    // One witness per flow class is kept per desktop so that a lane which never ran is visible in
    // the record instead of being averaged into a total that still looks plausible. The arm-wide
    // values are summed from these, so a witness and its total cannot drift apart.
    internal long[] PageConnectionsPerDesktop { get; }

    internal long[] BulkFramesPerDesktop { get; }

    internal long[] DnsSentPerDesktop { get; }

    internal long PageConnections => Total(PageConnectionsPerDesktop);

    internal long BulkFrames => Total(BulkFramesPerDesktop);

    internal long DnsSent => Total(DnsSentPerDesktop);

    internal long _pages;
    internal long _pageMessages;
    internal long _pageBytes;
    internal long _pageErrors;
    internal long _bulkBytes;
    internal long _bulkBytesSent;
    internal long _bulkErrors;
    internal long _dnsAnswered;
    internal long _dnsServfail;
    internal long _dnsTimeout;
    internal long _dnsOther;
    internal long _udpBytes;

    private static long Total(long[] perDesktop)
    {
        long total = 0;
        foreach (var value in perDesktop)
        {
            total += value;
        }

        return total;
    }
}
