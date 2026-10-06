using System.Globalization;
using System.Text;
using System.Text.Json;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client;

/// <summary>
/// The plan schema and its loader. A plan is <c>{"arms": [...]}</c>; every arm needs <c>name</c> and
/// <c>kind</c>, and the remaining keys are parsed for every arm but read only by the arms that know
/// them:
/// <list type="bullet">
/// <item><c>seconds</c> -- every arm (default 60).</item>
/// <item><c>protocol</c> ("tcp", "udp" or "tcp+udp") -- latency, and the base latency phase; the
/// loss phase always runs udp.</item>
/// <item><c>ratePerSecond</c> -- latency, loss, dns.</item>
/// <item><c>payloadBytes</c> -- latency, loss, persistent.</item>
/// <item><c>window</c> -- the in-flight window: latency, loss, and both base phases. It is a count
/// of requests, not a duration; the duration key is <c>lossWindowMs</c>.</item>
/// <item><c>lanes</c> -- latency, and both base phases.</item>
/// <item><c>lossWindowMs</c> -- the UDP loss threshold in milliseconds: loss, mix, and the base
/// loss phase.</item>
/// <item><c>modeMix</c>, <c>connectionsPerSecond</c>, <c>expectedBytes</c> -- reliability.</item>
/// <item><c>expectedBytes</c> -- also persistent, which announces it to the target.</item>
/// <item><c>streams</c>, <c>targetBytesPerSecond</c> -- throughput.</item>
/// <item><c>tcpPercent</c>, <c>cnameEvery</c>, <c>dnsPort</c> -- dns. <c>cnameEvery</c> greater than
/// zero replaces every n-th query with a CNAME query, which is why the arm publishes the measured
/// mix as <c>metrics.queryTypes</c> rather than a declared one.</item>
/// <item><c>desktops</c> -- mix.</item>
/// <item><c>intervalMs</c>, <c>idleSeconds</c> -- persistent.</item>
/// </list>
/// </summary>
internal static class PlanFile
{
    private const string DefaultPlanJson = """
        {
          "arms": [
            { "name": "LAT", "kind": "latency", "seconds": 60, "ratePerSecond": 20, "payloadBytes": 120, "protocol": "tcp+udp" },
            { "name": "LOSS", "kind": "loss", "seconds": 120, "ratePerSecond": 500, "payloadBytes": 200 },
            { "name": "REL", "kind": "reliability", "seconds": 120, "connectionsPerSecond": 20, "modeMix": "clean=25,resetAfterN=25,partialFin=25,halfClose=25" },
            { "name": "THRU", "kind": "throughput", "seconds": 60, "streams": 4, "targetBytesPerSecond": 25000000 },
            { "name": "DNS", "kind": "dns", "seconds": 90, "ratePerSecond": 200, "tcpPercent": 2, "cnameEvery": 0 },
            { "name": "MIX", "kind": "mix", "seconds": 120, "desktops": 4 },
            { "name": "IDLE", "kind": "idle", "seconds": 60 },
            { "name": "BASE", "kind": "base", "seconds": 60 }
          ]
        }
        """;

    private static readonly string[] s_knownKinds =
    [
        "latency",
        "loss",
        "reliability",
        "throughput",
        "dns",
        "mix",
        "idle",
        "persistent",
        "base",
    ];

    internal static bool TryLoad(string? path, out List<ArmSpec> arms, out byte[] planBytes, out string? error)
    {
        arms = [];
        error = null;
        planBytes = [];

        if (!TryReadPlanBytes(path, out planBytes, out error))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(planBytes);
        }
        catch (JsonException exception)
        {
            error = $"plan is not valid JSON: {exception.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("arms", out var armElements)
                || armElements.ValueKind != JsonValueKind.Array)
            {
                error = "plan must be an object with an 'arms' array";
                return false;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in armElements.EnumerateArray())
            {
                if (!TryReadArm(element, out var spec, out error))
                {
                    return false;
                }

                if (!names.Add(spec.Name))
                {
                    error = $"duplicate arm name '{spec.Name}'";
                    return false;
                }

                arms.Add(spec);
            }

            if (arms.Count == 0)
            {
                error = "plan contains no arms";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadPlanBytes(string? path, out byte[] planBytes, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(path))
        {
            planBytes = Encoding.UTF8.GetBytes(DefaultPlanJson);
            return true;
        }

        try
        {
            planBytes = File.ReadAllBytes(path);
            return true;
        }
        catch (IOException exception)
        {
            planBytes = [];
            error = $"cannot read plan '{path}': {exception.Message}";
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            planBytes = [];
            error = $"cannot read plan '{path}': {exception.Message}";
            return false;
        }
    }

    internal static bool TryParseModeMix(string text, out List<ModeWeight> weights, out string? error)
    {
        weights = [];
        error = null;
        var total = 0;

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                error = $"modeMix entry '{part}' is not <mode>=<weight>";
                return false;
            }

            var modeText = part[..separator];
            var weightText = part[(separator + 1)..];
            if (!TryParseMode(modeText, out var mode))
            {
                error = $"modeMix entry '{modeText}' is not a known TCP mode";
                return false;
            }

            if (!int.TryParse(weightText, NumberStyles.None, CultureInfo.InvariantCulture, out var weight) || weight < 0)
            {
                error = $"modeMix weight '{weightText}' is not a non-negative integer";
                return false;
            }

            weights.Add(new ModeWeight(mode, weight));
            total += weight;
        }

        if (total <= 0)
        {
            error = "modeMix must contain at least one positive weight";
            return false;
        }

        return true;
    }

    private static bool TryParseMode(string text, out TcpMode mode)
    {
        mode = text switch
        {
            "clean" => TcpMode.Clean,
            "resetAfterN" => TcpMode.ResetAfterN,
            "partialFin" => TcpMode.PartialFin,
            "halfClose" => TcpMode.HalfClose,
            "stall" => TcpMode.Stall,
            _ => (TcpMode)255,
        };
        return (byte)mode != 255;
    }

    internal static string SanitizeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        }

        return builder.Length == 0 ? "arm" : builder.ToString();
    }

    private static bool TryReadArm(JsonElement element, out ArmSpec spec, out string? error)
    {
        spec = new ArmSpec();
        error = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "every arm must be a JSON object";
            return false;
        }

        if (!TryReadText(element, "name", out var name) || name.Length == 0)
        {
            error = "every arm needs a non-empty 'name'";
            return false;
        }

        if (!TryReadText(element, "kind", out var kind) || Array.IndexOf(s_knownKinds, kind) < 0)
        {
            error = $"arm '{name}' has an unknown 'kind'";
            return false;
        }

        spec.Name = name;
        spec.Kind = kind;

        if (element.TryGetProperty("seconds", out _))
        {
            if (!TryReadNumber(element, "seconds", out var seconds) || seconds <= 0)
            {
                error = $"arm '{name}' has an invalid 'seconds'";
                return false;
            }

            spec.Seconds = seconds;
        }

        ReadArmNumbers(element, spec);
        spec.Protocol = TryReadText(element, "protocol", out var protocol) ? protocol : "tcp";
        spec.ModeMix = TryReadText(element, "modeMix", out var modeMix) ? modeMix : ArmSpec.DefaultModeMix;

        if (spec.Protocol is not ("tcp" or "udp" or "tcp+udp"))
        {
            error = $"arm '{name}' has protocol '{spec.Protocol}' (expected tcp, udp or tcp+udp)";
            return false;
        }

        return true;
    }

    private static void ReadArmNumbers(JsonElement element, ArmSpec spec)
    {
        spec.RatePerSecond = ReadInt(element, "ratePerSecond", 0);
        spec.PayloadBytes = ReadInt(element, "payloadBytes", 0);
        spec.ConnectionsPerSecond = ReadInt(element, "connectionsPerSecond", 0);
        spec.Streams = ReadInt(element, "streams", 0);
        spec.TcpPercent = ReadInt(element, "tcpPercent", 0);
        spec.CnameEvery = ReadInt(element, "cnameEvery", 0);
        spec.Desktops = ReadInt(element, "desktops", 0);
        spec.Window = ReadInt(element, "window", 0);
        spec.LossWindowMs = ReadInt(element, "lossWindowMs", 0);
        spec.DnsPort = ReadInt(element, "dnsPort", 0);
        spec.Lanes = ReadInt(element, "lanes", 0);
        spec.IntervalMs = ReadInt(element, "intervalMs", 0);
        spec.IdleSeconds = ReadInt(element, "idleSeconds", 0);
        spec.ExpectedBytes = ReadInt(element, "expectedBytes", 0);
        spec.TargetBytesPerSecond = ReadInt(element, "targetBytesPerSecond", 0);
    }

    private static int ReadInt(JsonElement element, string name, int fallback) =>
        TryReadInt(element, name, out var value) ? value : fallback;

    private static bool TryReadText(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (property.TryGetInt32(out value))
        {
            return true;
        }

        if (!property.TryGetDouble(out var asDouble) || Math.Abs(asDouble - Math.Round(asDouble, MidpointRounding.ToEven)) > 1e-9)
        {
            return false;
        }

        value = (int)asDouble;
        return true;
    }

    private static bool TryReadNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }
}
