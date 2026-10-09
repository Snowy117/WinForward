using System.Text.Json;
using WinForward.E2E.Cli;
using WinForward.E2E.Client;
using Xunit;

namespace WinForward.E2E.Tests;

/// <summary>
/// The arm-level failure boundary of a whole client run. The plan used here is legal and its
/// arm observes the token as its only work, so the run is decided by the cancellation alone and not
/// by a target, a socket or a clock.
/// </summary>
public sealed class ClientRunnerTests
{
    private static readonly string[] s_errorRecordKeys =
        ["type", "arm", "kind", "label", "error", "message", "detail", "startedTicks", "endedTicks"];

    // A cancellation is booked exactly like an arm-level error -- the record exists, the arm and the
    // run are marked failed and the client exits 1 -- so no arm can end without evidence. The message
    // stays the fixed literal the harness has always written for it while `error` names the
    // cancellation, so a consumer can still tell a stopped run from a broken arm.
    [Fact]
    public async Task ACancelledArmLeavesAnErrorRecordAndAFailedRunFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wf-e2e-cancel-{Guid.NewGuid():N}");
        // The cleanup has to be able to delete the directory even when the run fails before creating
        // it, or a DirectoryNotFoundException from the finally would replace the real failure.
        Directory.CreateDirectory(directory);
        try
        {
            var options = new ClientOptions
            {
                TargetAddress = "127.0.0.1",
                PlanPath = RepoPaths.Tier0Plan("minimal-idle.json"),
                OutDirectory = directory,
                Label = "cancel",
            };

            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            var status = await ClientRunner.RunAsync(options, cancellation.Token);

            Assert.Equal(ExitCodes.RuntimeError, status);

            var lines = await File.ReadAllLinesAsync(Path.Combine(directory, "IDLE.jsonl"), CancellationToken.None);
            // The sampler writes `sample` records into the same file from its own 1 Hz loop, so the
            // error record is located by its type: the ordered contract is error-before-result, not
            // "the first line of the file".
            var types = new string?[lines.Length];
            for (var index = 0; index < lines.Length; index++)
            {
                using var document = JsonDocument.Parse(lines[index]);
                types[index] = document.RootElement.GetProperty("type").GetString();
            }

            var errorIndex = Array.IndexOf(types, "error");
            Assert.True(errorIndex >= 0, "the arm file carries no error record");
            // One per arm, whichever step of the failure boundary raised it: a second record
            // would mean the arm's failure was booked twice.
            Assert.Equal(1, types.Count(static type => type == "error"));
            Assert.True(errorIndex < Array.IndexOf(types, "result"), "the error record must precede the arm's result record");

            using var first = JsonDocument.Parse(lines[errorIndex]);
            var record = first.RootElement;
            Assert.Equal(s_errorRecordKeys, record.EnumerateObject().Select(property => property.Name));
            Assert.Equal("error", record.GetProperty("type").GetString());
            Assert.Equal("IDLE", record.GetProperty("arm").GetString());
            Assert.Equal("idle", record.GetProperty("kind").GetString());
            Assert.Equal("cancel", record.GetProperty("label").GetString());
            Assert.Equal("OperationCanceledException", record.GetProperty("error").GetString());
            Assert.Equal("cancelled", record.GetProperty("message").GetString());

            // `detail` is the innermost exception's own type. Which await observes the token
            // decides the concrete subtype -- Task.Delay raises TaskCanceledException -- so the
            // assertion accepts the cancellation family rather than one member of it.
            var detail = record.GetProperty("detail").GetString();
            Assert.True(detail is "TaskCanceledException" or "OperationCanceledException", $"unexpected detail '{detail}'");

            using var runDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(directory, "run.json"), CancellationToken.None));
            var run = runDocument.RootElement;
            Assert.True(run.GetProperty("failed").GetBoolean());
            Assert.True(run.GetProperty("arms")[0].GetProperty("failed").GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
