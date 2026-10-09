using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using WinForward.E2E.Cli;
using WinForward.E2E.Contracts;
using WinForward.E2E.Target;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The target's receive-loop count, end to end: the option a run declares decides what the listeners
/// start, and the count the run ended with is the one its ledger publishes.
/// </summary>
/// <remarks>
/// The facts run a whole target -- the runner, all four listeners, a real datagram through the echo
/// listener and a ledger file -- rather than one listener on its own, because the option is the thing
/// being pinned: a run that parsed the count and then ignored it would pass every fact about a listener
/// built with an explicit count and still publish nothing about the concurrency it actually ran. The
/// receive loops' own counters are measured elsewhere, so this file is the chain from the command line
/// to the published number and not a second measurement of the loops.
/// </remarks>
public sealed class UdpReceiverOptionTests
{
    private const string Label = "receivers";

    private const int DeclaredReceivers = 3;

    [Fact]
    public async Task TheDeclaredReceiveLoopCountIsTheOneTheRunStartsAndPublishes()
    {
        Assert.Equal(DeclaredReceivers, await UdpReceiversAsync("--udp-receivers", DeclaredReceivers.ToString(CultureInfo.InvariantCulture)));
    }

    // The option's default is the formula the target has always used, and the formula is stated here
    // rather than read from the constant it was moved into: a run that declares nothing has to keep
    // behaving as it did before the option existed, whatever the constant later says.
    [Fact]
    public async Task AnUndeclaredReceiveLoopCountKeepsTheDefaultFormula()
    {
        Assert.Equal(Math.Clamp(Environment.ProcessorCount / 2, 2, 8), await UdpReceiversAsync());
    }

    /// <summary>
    /// One whole target run: the option line, ports nothing else holds, and a ledger file. The run is
    /// ended by its token once the echo listener has answered a datagram, which is what makes the
    /// published count the count of a listener that served rather than of one that only bound.
    /// </summary>
    private static async Task<double> UdpReceiversAsync(params string[] extraArguments)
    {
        var tcpPort = ArmRunFixture.FreePort(SocketType.Stream, ProtocolType.Tcp);
        var udpPort = ArmRunFixture.FreePort(SocketType.Dgram, ProtocolType.Udp);
        var dnsPort = ArmRunFixture.FreeDualPort();
        var ledgerPath = Path.Combine(Path.GetTempPath(), $"wf-e3d-receivers-{Guid.NewGuid():N}.jsonl");

        string[] arguments =
        [
            "--bind", "127.0.0.1",
            "--tcp-port", tcpPort.ToString(CultureInfo.InvariantCulture),
            "--udp-port", udpPort.ToString(CultureInfo.InvariantCulture),
            "--dns-port", dnsPort.ToString(CultureInfo.InvariantCulture),
            "--label", Label,
            "--ledger", ledgerPath,
            .. extraArguments,
        ];

        Assert.True(TargetOptions.TryCreate(arguments, out var options, out var error), error);

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = TargetRunner.RunAsync(options, shutdown.Token);
        try
        {
            await ArmRunFixture.EchoOneDatagramAsync(udpPort, shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            Assert.Equal(ExitCodes.Success, await running.ConfigureAwait(false));
        }

        var ledger = await File.ReadAllTextAsync(ledgerPath).ConfigureAwait(false);
        File.Delete(ledgerPath);

        return Number(ledger, ArmKeys.Ledger.TargetSummary.UdpTotals.UdpReceivers);
    }

    /// <summary>
    /// The number one path carries in the run's <c>targetSummary</c>, one level down under <c>udp</c>.
    /// </summary>
    private static double Number(string ledger, string path) =>
        JsonPaths
            .FlattenJsonl(LineOf(ledger, "targetSummary"))
            [$"{ArmKeys.Ledger.TargetSummary.Udp}/{path}"][0]
            .Value.GetDouble();

    /// <summary>
    /// The one ledger line of a kind: the runner writes one <c>targetSummary</c> at the end of the run,
    /// so a second line would mean the fixture drove more than the fact says it did.
    /// </summary>
    private static string LineOf(string ledger, string kind)
    {
        var found = new List<string>();
        foreach (var line in ledger.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty(ArmKeys.Common.Record.Type).GetString() == kind)
            {
                found.Add(line);
            }
        }

        return Assert.Single(found);
    }
}
