using System.Globalization;
using System.Text;
using System.Text.Json;
using WinForward.E2E.Client.Arms;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client;

/// <summary>
/// The plan schema and its loader. A plan is <c>{"arms": [...]}</c>; every arm needs <c>name</c> and
/// <c>kind</c>, and may declare only the keys its kind reads (see <see cref="ArmKind"/>):
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
    /// <summary>
    /// An arm name becomes an output file name, and the file name has to survive a path component on
    /// every platform the harness runs on. The limit applies to the sanitized name, without the
    /// <c>.jsonl</c> suffix.
    /// </summary>
    private const int MaxArmFileNameLength = 128;

    /// <summary>
    /// How far a JSON number may sit from an integer and still name one. Only floating point noise
    /// should land inside it: <c>100.5</c> on an integer key is a load error, not a rounding.
    /// </summary>
    private const double IntegerTolerance = 1e-9;

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

    private readonly record struct NumberKey(string Name, int Minimum, int Maximum, Action<ArmSpec, int> Assign);

    private enum IntReadOutcome
    {
        Ok,
        NotAnInteger,
        OutOfRange,
    }

    // D14.14: zero means "not declared" for every numeric key, so the lower bound is zero throughout
    // and a declared negative value is a load error instead of a silent clamp; only dnsPort and
    // tcpPercent have a real ceiling. The arms keep their own Math.Max/Math.Clamp as a defence in
    // depth: those calls are not what makes an illegal value legal.
    private static readonly NumberKey[] s_numberKeys =
    [
        new("ratePerSecond", 0, int.MaxValue, static (spec, value) => spec.RatePerSecond = value),
        new("payloadBytes", 0, int.MaxValue, static (spec, value) => spec.PayloadBytes = value),
        new("connectionsPerSecond", 0, int.MaxValue, static (spec, value) => spec.ConnectionsPerSecond = value),
        new("streams", 0, int.MaxValue, static (spec, value) => spec.Streams = value),
        new("tcpPercent", 0, 100, static (spec, value) => spec.TcpPercent = value),
        new("cnameEvery", 0, int.MaxValue, static (spec, value) => spec.CnameEvery = value),
        new("desktops", 0, int.MaxValue, static (spec, value) => spec.Desktops = value),
        new("window", 0, int.MaxValue, static (spec, value) => spec.Window = value),
        new("lossWindowMs", 0, int.MaxValue, static (spec, value) => spec.LossWindowMs = value),
        new("dnsPort", 0, 65535, static (spec, value) => spec.DnsPort = value),
        new("lanes", 0, int.MaxValue, static (spec, value) => spec.Lanes = value),
        new("intervalMs", 0, int.MaxValue, static (spec, value) => spec.IntervalMs = value),
        new("idleSeconds", 0, int.MaxValue, static (spec, value) => spec.IdleSeconds = value),
        new("expectedBytes", 0, int.MaxValue, static (spec, value) => spec.ExpectedBytes = value),
        new("targetBytesPerSecond", 0, int.MaxValue, static (spec, value) => spec.TargetBytesPerSecond = value),
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

            return TryReadArms(armElements, out arms, out error);
        }
    }

    private static bool TryReadArms(JsonElement armElements, out List<ArmSpec> arms, out string? error)
    {
        arms = [];
        error = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
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

            // Two arm names can differ while their output file names collide, and the second arm
            // would truncate the first one's records: the check has to run on the name the file
            // is actually built from.
            var fileName = SanitizeFileName(spec.Name);
            if (fileName.Length > MaxArmFileNameLength)
            {
                error = $"arm '{spec.Name}' maps to a {fileName.Length}-character file name '{fileName}', above the {MaxArmFileNameLength}-character limit";
                return false;
            }

            if (!files.TryAdd(fileName, spec.Name))
            {
                error = $"arm names '{files[fileName]}' and '{spec.Name}' both map to the output file '{fileName}.jsonl'";
                return false;
            }

            arms.Add(spec);
        }

        if (arms.Count == 0)
        {
            error = "plan contains no arms";
            return false;
        }

        return true;
    }

    private static bool TryReadPlanBytes(string? path, out byte[] planBytes, out string? error)
    {
        error = null;

        // A missing path is the one way to ask for the built-in plan; an empty one is rejected by the
        // command line before it gets here (D14.1), so it never reaches this method.
        if (path is null)
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

        if (!TryReadText(element, "kind", out var kindName))
        {
            error = $"arm '{name}' needs a 'kind' of {ArmKind.KnownNames()}";
            return false;
        }

        if (ArmKind.Find(kindName) is not { } kind)
        {
            error = $"arm '{name}' has an unknown 'kind' '{kindName}' (expected one of: {ArmKind.KnownNames()})";
            return false;
        }

        spec.Name = name;
        spec.Kind = kind.Name;

        if (FindUnknownKey(element, kind) is { } unknownKey)
        {
            error = $"{Prefix(name, kind.Name)}unknown key '{unknownKey}' (expected one of: {string.Join(", ", kind.Keys)})";
            return false;
        }

        return TryReadArmBody(element, kind, spec, out error);
    }

    private static bool TryReadArmBody(JsonElement element, ArmKind kind, ArmSpec spec, out string? error)
    {
        error = null;

        if (element.TryGetProperty("seconds", out var secondsElement))
        {
            if (!TryReadNumber(element, "seconds", out var seconds) || seconds <= 0)
            {
                error = $"{Prefix(spec.Name, kind.Name)}'seconds' is {secondsElement.GetRawText()}, which is not a positive number";
                return false;
            }

            spec.Seconds = seconds;
        }

        if (!TryReadArmNumbers(element, kind, spec, out error))
        {
            return false;
        }

        var prefix = Prefix(spec.Name, kind.Name);
        if (!TryReadOptionalText(element, prefix, "protocol", "tcp", out var protocol, out error)
            || !TryReadOptionalText(element, prefix, "modeMix", ArmSpec.DefaultModeMix, out var modeMix, out error))
        {
            return false;
        }

        spec.Protocol = protocol;
        spec.ModeMix = modeMix;

        if (spec.Protocol is not ("tcp" or "udp" or "tcp+udp"))
        {
            error = $"{prefix}protocol '{spec.Protocol}' is not one of tcp, udp or tcp+udp";
            return false;
        }

        if (kind.Validate(spec) is { } validationError)
        {
            error = prefix + validationError;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a key that may be absent: absent takes <paramref name="fallback"/>, present must be a
    /// string. Another JSON type is a load error rather than the fallback, for the same reason a
    /// fractional <c>window</c> is one: the plan declared a value, and reading it as the default
    /// would publish a run the plan never asked for.
    /// </summary>
    private static bool TryReadOptionalText(
        JsonElement element,
        string prefix,
        string name,
        string fallback,
        out string value,
        out string? error)
    {
        error = null;
        value = fallback;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            error = $"{prefix}'{name}' is {property.GetRawText()}, which is not a string";
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static string Prefix(string name, string kind) => $"arm '{name}' (kind '{kind}'): ";

    private static string? FindUnknownKey(JsonElement element, ArmKind kind) =>
        element.EnumerateObject()
            .Select(static property => property.Name)
            .FirstOrDefault(name => Array.IndexOf(kind.Keys, name) < 0);

    private static bool TryReadArmNumbers(JsonElement element, ArmKind kind, ArmSpec spec, out string? error)
    {
        error = null;
        foreach (var key in s_numberKeys)
        {
            // A key the kind does not read is already an unknown key, but the whole table is checked
            // against the element anyway: the descriptor says what the value must be, the whitelist
            // says whether the kind may declare it.
            if (!element.TryGetProperty(key.Name, out var property))
            {
                continue;
            }

            var read = TryReadInt(element, key.Name, out var value);
            if (read == IntReadOutcome.NotAnInteger)
            {
                error = $"{Prefix(spec.Name, kind.Name)}'{key.Name}' is {property.GetRawText()}, which is not an integer";
                return false;
            }

            if (read == IntReadOutcome.OutOfRange || value < key.Minimum || value > key.Maximum)
            {
                error = Prefix(spec.Name, kind.Name) + string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{key.Name}' is {property.GetRawText()}, which is outside {key.Minimum}..{key.Maximum}");
                return false;
            }

            key.Assign(spec, value);
        }

        return true;
    }

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

    /// <summary>
    /// Reads one integer key, telling "this is not an integer" apart from "this integer is out of
    /// range": a value past <see cref="int"/>'s bounds such as 3000000000 has no fractional part, so
    /// reporting it as "not an integer" would send the reader looking for one that is not there.
    /// </summary>
    private static IntReadOutcome TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return IntReadOutcome.NotAnInteger;
        }

        if (property.TryGetInt32(out value))
        {
            return IntReadOutcome.Ok;
        }

        if (property.TryGetInt64(out var asLong))
        {
            if (asLong is >= int.MinValue and <= int.MaxValue)
            {
                value = (int)asLong;
                return IntReadOutcome.Ok;
            }

            return IntReadOutcome.OutOfRange;
        }

        if (!property.TryGetDouble(out var asDouble))
        {
            // Past a double as well, so no fractional part can even be represented: out of range.
            return IntReadOutcome.OutOfRange;
        }

        if (Math.Abs(asDouble - Math.Round(asDouble, MidpointRounding.ToEven)) > IntegerTolerance)
        {
            return IntReadOutcome.NotAnInteger;
        }

        if (asDouble is < int.MinValue or > int.MaxValue)
        {
            return IntReadOutcome.OutOfRange;
        }

        value = (int)asDouble;
        return IntReadOutcome.Ok;
    }

    private static bool TryReadNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }
}
