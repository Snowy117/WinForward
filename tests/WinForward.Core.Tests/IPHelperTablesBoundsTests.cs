using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// Pure bounds validation of <see cref="IPHelperTables"/> owner tables (P0 R3): the
/// driver-written byte count must hold the announced row count before any row dereference —
/// fail closed, never clamp or partially read, so an inconsistent table yields "no
/// attribution" instead of out-of-bounds reads or a wrong PID.
/// </summary>
public sealed class IPHelperTablesBoundsTests
{
    [Fact]
    public void ExactRowFitPasses()
    {
        IPHelperTables.ValidateRowCount(rowCount: 2, bytesWritten: 4 + 2 * 8, rowSize: 8, tableName: "IPv4 TCP owner table");
    }

    [Fact]
    public void OverflowByOneRowThrowsNamingTheTable()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            IPHelperTables.ValidateRowCount(rowCount: 3, bytesWritten: 4 + 2 * 8, rowSize: 8, tableName: "IPv4 TCP owner table"));

        Assert.Contains("IPv4 TCP owner table", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverflowByOneByteThrows()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IPHelperTables.ValidateRowCount(rowCount: 2, bytesWritten: 4 + 2 * 8 - 1, rowSize: 8, tableName: "IPv6 UDP owner table"));
    }

    [Fact]
    public void ZeroRowsWithMinimumHeaderBufferPasses()
    {
        IPHelperTables.ValidateRowCount(rowCount: 0, bytesWritten: 4, rowSize: 8, tableName: "IPv4 UDP owner table");
    }

    [Fact]
    public void MessageCarriesRowCountAndBytesWritten()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            IPHelperTables.ValidateRowCount(rowCount: 7, bytesWritten: 100, rowSize: 16, tableName: "IPv6 TCP owner table"));

        Assert.Contains("7", exception.Message, StringComparison.Ordinal);
        Assert.Contains("100", exception.Message, StringComparison.Ordinal);
        Assert.Contains("16", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeRowCountThrows()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IPHelperTables.ValidateRowCount(rowCount: -1, bytesWritten: 4, rowSize: 8, tableName: "IPv4 TCP owner table"));
    }
}
