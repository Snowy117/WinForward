using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinForward.Benchmarks.Stability;

internal sealed class StabilityContext : IDisposable
{
    private static readonly JsonSerializerOptions Serialization = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly TextWriter? _fileOutput;

    public StabilityContext(SoakOptions options, TextWriter? fileOutput)
    {
        _fileOutput = fileOutput;
        var metadata = new
        {
            Type = "metadata",
            SchemaVersion = 2,
            Mode = "stability",
            ManagedOnly = true,
            TimestampUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Options = options,
        };
        Write(JsonSerializer.Serialize(metadata, Serialization));
    }

    public void WriteResult(string scenario, object parameters, object metrics)
    {
        var record = new { Type = "result", Scenario = scenario, Parameters = parameters, Metrics = metrics };
        Write(JsonSerializer.Serialize(record, Serialization));
    }

    private void Write(string json)
    {
        Console.Out.WriteLine(json);
        if (_fileOutput is not null)
        {
            _fileOutput.WriteLine(json);
            _fileOutput.Flush();
        }
    }

    public void Dispose()
    {
        _fileOutput?.Dispose();
    }
}
