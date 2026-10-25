using System.Text.Json.Serialization;

namespace WinForward.Configuration;

/// <summary>
/// The optional <c>WinForward.BackendDriver</c> section: which interception backend serves the run,
/// plus the provider-specific object that backend owns.
/// </summary>
public sealed class BackendDriverDto
{
    [JsonPropertyName("Provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("WinpkFilter")]
    public WinpkFilterDriverDto? WinpkFilter { get; init; }

    [JsonPropertyName("WinForwardKernel")]
    public WinForwardKernelDriverDto? WinForwardKernel { get; init; }
}

/// <summary>The settings only the WinpkFilter backend reads.</summary>
public sealed class WinpkFilterDriverDto
{
    [JsonPropertyName("KernelPassCache")]
    public bool? KernelPassCache { get; init; }
}

/// <summary>The settings only the WinForward.Kernel backend reads.</summary>
public sealed class WinForwardKernelDriverDto
{
    [JsonPropertyName("DriverServiceName")]
    public string? DriverServiceName { get; init; }
}

/// <summary>The interception backend a run selects; exactly one backend is active per run.</summary>
public enum BackendProvider
{
    /// <summary>The WinpkFilter/NDISAPI packet backend, the default.</summary>
    WinpkFilter,

    /// <summary>The WinForward.Kernel WFP backend.</summary>
    WinForwardKernel,
}

/// <summary>
/// The resolved backend selection: the provider, its driver-identity settings, and the
/// WinpkFilter-only pass-mirror opt-in that both provider shapes must still resolve.
/// </summary>
public sealed record BackendDriverConfiguration(BackendProvider Provider, bool KernelPassCache, string DriverServiceName)
{
    /// <summary>The canonical spelling of <see cref="BackendProvider.WinpkFilter"/> in configuration and logs.</summary>
    public const string WinpkFilterProviderName = "WinpkFilter";

    /// <summary>The canonical spelling of <see cref="BackendProvider.WinForwardKernel"/> in configuration and logs.</summary>
    public const string KernelProviderName = "WinForward.Kernel";

    /// <summary>The Kernel driver's device/service name when the configuration does not name one.</summary>
    public const string DefaultDriverServiceName = "wfprobe";

    /// <summary>An omitted <c>BackendDriver</c> section: the WinpkFilter backend with the code defaults.</summary>
    public static BackendDriverConfiguration Default { get; } =
        new(BackendProvider.WinpkFilter, KernelPassCache: false, DriverServiceName: DefaultDriverServiceName);

    /// <summary>The provider's canonical configuration name; enum names are not the accepted values.</summary>
    public string ProviderName => Provider switch
    {
        BackendProvider.WinpkFilter => WinpkFilterProviderName,
        BackendProvider.WinForwardKernel => KernelProviderName,
        _ => throw new InvalidOperationException($"Unknown backend provider '{Provider}'."),
    };
}

/// <summary>
/// Resolves <see cref="WinForwardConfigDto.BackendDriver"/> into the one <see cref="BackendDriverConfiguration"/>
/// a run carries. Absent section, absent provider, or an explicitly null provider all mean WinpkFilter;
/// the two canonical provider values are matched case-insensitively and every other value is a
/// path-qualified error.
/// </summary>
internal static class ConfigurationBackendDriver
{
    private const string SectionPath = "WinForward.BackendDriver";
    private const string LegacyKernelPassCachePath = "WinForward.KernelPassCache";

    /// <summary>
    /// Resolves the section, appending every diagnostic it finds. A failed provider resolution returns
    /// the default and skips the provider-specific checks: the configuration already fails, and a
    /// second guess about which provider object was meant would misreport the operator's file.
    /// </summary>
    internal static BackendDriverConfiguration Resolve(WinForwardConfigDto dto, List<ConfigDiagnostic> errors, List<ConfigDiagnostic> warnings)
    {
        var section = dto.BackendDriver;
        if (!TryParseProvider(section?.Provider, errors, out var provider)) return BackendDriverConfiguration.Default;

        RejectUnselectedProviderObject(section, provider, errors);
        var kernelPassCache = ResolveKernelPassCache(dto, section, provider, errors, warnings);
        var driverServiceName = ResolveDriverServiceName(section?.WinForwardKernel, provider, errors);
        return new BackendDriverConfiguration(provider, kernelPassCache, driverServiceName);
    }

    private static bool TryParseProvider(string? raw, List<ConfigDiagnostic> errors, out BackendProvider provider)
    {
        provider = BackendProvider.WinpkFilter;
        if (raw is null) return true;

        var value = raw.Trim();
        if (value.Equals(BackendDriverConfiguration.KernelProviderName, StringComparison.OrdinalIgnoreCase))
        {
            provider = BackendProvider.WinForwardKernel;
            return true;
        }

        if (value.Equals(BackendDriverConfiguration.WinpkFilterProviderName, StringComparison.OrdinalIgnoreCase)) return true;

        errors.Add(new($"{SectionPath}.Provider", $"Provider must be {BackendDriverConfiguration.WinpkFilterProviderName} or {BackendDriverConfiguration.KernelProviderName}."));
        return false;
    }

    /// <summary>
    /// Rejects the provider object the selected provider does not own. Presence is an error even when
    /// its value is the default: an object is a statement that the operator meant to configure that
    /// backend, and silently ignoring it would hide the misselection this section exists to surface.
    /// </summary>
    private static void RejectUnselectedProviderObject(BackendDriverDto? section, BackendProvider provider, List<ConfigDiagnostic> errors)
    {
        if (provider == BackendProvider.WinForwardKernel)
        {
            if (section?.WinpkFilter is not null) errors.Add(new($"{SectionPath}.WinpkFilter", $"This object configures the {BackendDriverConfiguration.WinpkFilterProviderName} provider; remove it or set Provider to {BackendDriverConfiguration.WinpkFilterProviderName}."));
            return;
        }

        if (section?.WinForwardKernel is not null) errors.Add(new($"{SectionPath}.WinForwardKernel", $"This object configures the {BackendDriverConfiguration.KernelProviderName} provider; remove it or set Provider to {BackendDriverConfiguration.KernelProviderName}."));
    }

    /// <summary>
    /// Resolves the pass-mirror opt-in from its canonical nested location, from the legacy top-level
    /// alias, or from neither. The legacy alias is a one-release migration aid: it still resolves,
    /// warns with the canonical path, and fails as ambiguous when both locations are set.
    /// </summary>
    private static bool ResolveKernelPassCache(WinForwardConfigDto dto, BackendDriverDto? section, BackendProvider provider, List<ConfigDiagnostic> errors, List<ConfigDiagnostic> warnings)
    {
        if (provider == BackendProvider.WinForwardKernel)
        {
            // The nested WinpkFilter object is already an unselected-provider error, so only the legacy
            // top-level key reaches this branch.
            if (dto.KernelPassCache is not null) errors.Add(new(LegacyKernelPassCachePath, $"KernelPassCache is a {BackendDriverConfiguration.WinpkFilterProviderName} setting and is not valid under Provider = {BackendDriverConfiguration.KernelProviderName}; remove it."));
            return false;
        }

        var nested = section?.WinpkFilter?.KernelPassCache;
        if (nested is not null && dto.KernelPassCache is not null)
        {
            errors.Add(new(LegacyKernelPassCachePath, $"KernelPassCache is ambiguous: it is set both here and at {SectionPath}.{BackendDriverConfiguration.WinpkFilterProviderName}.KernelPassCache; keep only the nested key."));
            return false;
        }

        if (nested is { } value) return value;
        if (dto.KernelPassCache is not { } legacy) return false;

        warnings.Add(new(LegacyKernelPassCachePath, $"The top-level KernelPassCache key is accepted for one migration window; move it to {SectionPath}.{BackendDriverConfiguration.WinpkFilterProviderName}.KernelPassCache."));
        return legacy;
    }

    /// <summary>
    /// Resolves the Kernel driver identity. It is inert under WinpkFilter (whose run rejects the whole
    /// <c>WinForwardKernel</c> object), so only the Kernel provider validates its value.
    /// </summary>
    private static string ResolveDriverServiceName(WinForwardKernelDriverDto? section, BackendProvider provider, List<ConfigDiagnostic> errors)
    {
        if (provider != BackendProvider.WinForwardKernel) return BackendDriverConfiguration.DefaultDriverServiceName;
        if (section?.DriverServiceName is not { } raw) return BackendDriverConfiguration.DefaultDriverServiceName;

        var name = raw.Trim();
        if (name.Length > 0) return name;

        errors.Add(new($"{SectionPath}.WinForwardKernel.DriverServiceName", $"DriverServiceName must not be empty; omit the key for the default '{BackendDriverConfiguration.DefaultDriverServiceName}'."));
        return BackendDriverConfiguration.DefaultDriverServiceName;
    }
}
