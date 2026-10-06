using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Contracts.Json;
using WinForward.E2E.Target;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The ledger record shape the target publishes: absolute time first, then the label the target was
/// started with, then the record's own properties, one line each. The ledger crosses between two
/// independently deployed binaries, so this is frozen against the production envelope and the
/// shared sink rather than against whatever the writer happens to emit.
/// </summary>
public sealed class JsonlSinkLedgerTests
{
    private static readonly string[] s_expectedKeys = ["utc", "label", "type", "value"];

    private static readonly int[] s_expectedIndexes = [0, 1, 2];

    [Fact]
    public async Task OneRecordIsUtcThenLabelThenTheBodyAndEndsWithANewline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf-e2e-ledger-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var ledger = new JsonlSink(path, JsonlPolicy.SwallowAndCount, TargetRunner.WriteLedgerEnvelope("probe-label")))
            {
                await ledger.WriteAsync(
                    static writer =>
                    {
                        writer.WriteString("type", "probe");
                        writer.WriteNumber("value", 7);
                    },
                    CancellationToken.None);

                Assert.Equal(0, ledger.WriteErrors);
            }

            var text = await File.ReadAllTextAsync(path, CancellationToken.None);
            Assert.EndsWith("\n", text, StringComparison.Ordinal);

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var line = Assert.Single(lines);
            // The order is the contract: every record starts with the absolute time and the label
            // before whatever body the writer added, because a ledger timestamp that is not first
            // cannot be read positionally and the label is what attributes the row to an arm.
            Assert.StartsWith("{\"utc\":\"", line, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            Assert.Equal(s_expectedKeys, record.EnumerateObject().Select(property => property.Name));

            var utc = record.GetProperty("utc").GetString();
            Assert.True(
                DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed),
                $"the ledger timestamp '{utc}' is not round-trippable");
            Assert.Equal(TimeSpan.Zero, parsed.Offset);
            Assert.Equal("probe-label", record.GetProperty("label").GetString());
            Assert.Equal("probe", record.GetProperty("type").GetString());
            Assert.Equal(7, record.GetProperty("value").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EveryWriteAppendsExactlyOneRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf-e2e-ledger-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var ledger = new JsonlSink(path, JsonlPolicy.SwallowAndCount, TargetRunner.WriteLedgerEnvelope("append")))
            {
                for (var index = 0; index < 3; index++)
                {
                    var value = index;
                    await ledger.WriteAsync(writer => writer.WriteNumber("index", value), CancellationToken.None);
                }
            }

            var lines = (await File.ReadAllTextAsync(path, CancellationToken.None))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            Assert.Equal(s_expectedIndexes, lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("index").GetInt32()));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
