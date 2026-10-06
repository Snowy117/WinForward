using System.Globalization;
using System.Text.Json;
using WinForward.E2E.Target;
using Xunit;

namespace WinForward.E2E.Tests;

public sealed class LedgerWriterTests
{
    private static readonly string[] s_expectedKeys = ["utc", "label", "type", "value"];

    private static readonly int[] s_expectedIndexes = [0, 1, 2];

    [Fact]
    public async Task OneRecordIsUtcThenLabelThenTheBodyAndEndsWithANewline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf-e2e-ledger-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using (var ledger = new LedgerWriter(path, "probe-label"))
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
            // The order is the contract: analyze.py reads the envelope keys positionally in the
            // sense that every record starts with the absolute time and the label the target ran
            // with, before whatever body the writer added.
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
            await using (var ledger = new LedgerWriter(path, "append"))
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
