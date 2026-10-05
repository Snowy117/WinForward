using System.Globalization;
using System.Net;
using WinForward.Core;

namespace WinForward.Configuration;

/// <summary>
/// The target-declaration half of configuration validation: it parses <c>socks5Servers</c> and
/// <c>localTargets</c> into the single name-keyed <see cref="ProxyTarget"/> table a rule's target
/// resolves against, and it raises the non-blocking warnings those declarations carry. The loader
/// keeps the public defaults and the object graph; this collaborator owns the checks that normalize
/// each declaration — the seam <see cref="ConfigurationRules"/> occupies for rule parsing.
/// </summary>
internal static class ConfigurationTargets
{
    /// <summary>
    /// Validates both declaration lists into one table. The order is load-bearing: SOCKS5 entries are
    /// added first, so a name declared in both lists is always reported against the
    /// <c>localTargets</c> entry (the second declaration of an already-taken name).
    /// </summary>
    internal static void Validate(WinForwardConfigDto dto, Dictionary<string, ProxyTarget> targets, List<ConfigDiagnostic> errors, List<ConfigDiagnostic> warnings)
    {
        ValidateServers(dto, targets, errors);
        ValidateLocalTargets(dto, targets, errors, warnings);
    }

    private static void ValidateServers(WinForwardConfigDto dto, Dictionary<string, ProxyTarget> targets, List<ConfigDiagnostic> errors)
    {
        if (dto.Socks5Servers is null)
        {
            errors.Add(new("socks5Servers", "Field is required."));
            return;
        }

        for (var index = 0; index < dto.Socks5Servers.Count; index++)
        {
            ValidateServer(dto.Socks5Servers[index], index, targets, errors);
        }
    }

    private static void ValidateServer(Socks5ServerDto? dto, int index, Dictionary<string, ProxyTarget> targets, List<ConfigDiagnostic> errors)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"socks5Servers[{index}]");
        if (dto is null)
        {
            errors.Add(new(path, "Server entry must be an object."));
            return;
        }

        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name)) errors.Add(new($"{path}.name", "Name is required."));
        else if (targets.ContainsKey(name)) errors.Add(new($"{path}.name", "Name must be unique (case-insensitive)."));

        var host = dto.Host?.Trim();
        if (string.IsNullOrEmpty(host) || !IsValidHost(host)) errors.Add(new($"{path}.host", "Host must be an IP literal or DNS hostname."));
        if (dto.Port is < 1 or > 65535) errors.Add(new($"{path}.port", "Port must be in 1..65535."));
        if ((dto.Username is null) != (dto.Password is null)) errors.Add(new(path, "username and password must be supplied together."));
        if (dto.Username is not null && (dto.Username.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(dto.Username) > 255)) errors.Add(new($"{path}.username", "Username must be 1..255 UTF-8 bytes."));
        if (dto.Password is not null && (dto.Password.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(dto.Password) > 255)) errors.Add(new($"{path}.password", "Password must be 1..255 UTF-8 bytes."));

        if (name is not null && host is not null && IsValidHost(host) && dto.Port is >= 1 and <= 65535 && !targets.ContainsKey(name))
        {
            targets.Add(name, ProxyTarget.FromServer(new Socks5Server(name, host, (ushort)dto.Port, dto.Username, dto.Password)));
        }
    }

    private static void ValidateLocalTargets(WinForwardConfigDto dto, Dictionary<string, ProxyTarget> targets, List<ConfigDiagnostic> errors, List<ConfigDiagnostic> warnings)
    {
        if (dto.LocalTargets is null) return;
        for (var index = 0; index < dto.LocalTargets.Count; index++)
        {
            ValidateLocalTarget(dto.LocalTargets[index], index, targets, errors, warnings);
        }
    }

    /// <summary>
    /// Validates one <c>localTargets</c> entry. A local target's <c>host</c> must be an IP literal
    /// (<see cref="IPAddress.TryParse(string, out IPAddress)"/>, not the hostname-admitting <see cref="IsValidHost"/>): a
    /// hostname here would need DNS to configure the very endpoint the DNS path is being pointed at,
    /// so it fails closed instead. Names share one namespace with <c>socks5Servers</c>, and an
    /// accepted non-loopback address collects the endpoint-locality warning below.
    /// </summary>
    private static void ValidateLocalTarget(LocalTargetDto? dto, int index, Dictionary<string, ProxyTarget> targets, List<ConfigDiagnostic> errors, List<ConfigDiagnostic> warnings)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"localTargets[{index}]");
        if (dto is null)
        {
            errors.Add(new(path, "Local target entry must be an object."));
            return;
        }

        var name = dto.Name?.Trim();
        if (string.IsNullOrEmpty(name)) errors.Add(new($"{path}.name", "Name is required."));
        else if (targets.ContainsKey(name)) errors.Add(new($"{path}.name", "Name must be unique across socks5Servers and localTargets (case-insensitive)."));

        var host = dto.Host?.Trim();
        if (string.IsNullOrEmpty(host) || !IPAddress.TryParse(host, out var address))
        {
            errors.Add(new($"{path}.host", "Host must be an IP literal; a hostname would itself need DNS to be reachable."));
            return;
        }

        if (dto.Port is < 1 or > 65535) errors.Add(new($"{path}.port", "Port must be in 1..65535."));
        if (name is null || targets.ContainsKey(name) || dto.Port is < 1 or > 65535) return;

        targets.Add(name, new ProxyTarget(name, Socks5: null, new LocalTarget(name, Endpoint.From(address, (ushort)dto.Port))));
        WarnOnNonLoopbackLocalTarget(name, address, $"{path}.host", warnings);
    }

    /// <summary>
    /// Adds the endpoint-locality warning for a non-loopback local target, to the same
    /// <see cref="ValidatedConfiguration.Warnings"/> channel <see cref="ConfigurationLimits"/> writes
    /// its thresholds to. Non-loopback target addresses are accepted (a resolver on a trusted LAN
    /// stays configurable), so the warning is the operator's signal for the two consequences it
    /// names: the payload leaves this host in the clear, and the endpoint's replies are attributed to
    /// the flow's original destination.
    /// </summary>
    private static void WarnOnNonLoopbackLocalTarget(string name, IPAddress address, string path, List<ConfigDiagnostic> warnings)
    {
        if (IPAddress.IsLoopback(address)) return;
        warnings.Add(new(path, string.Create(CultureInfo.InvariantCulture, $"Local target '{name}' at {address} is not a loopback address: the flow's payload leaves this host in the clear, and the endpoint's replies are attributed to the flow's original destination.")));
    }

    private static bool IsValidHost(string host) => IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.Basic;
}
