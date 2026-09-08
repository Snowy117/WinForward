namespace WinForward.Windows;

public sealed record WindowsAdapter(
    string StableId,
    string FriendlyName,
    string InternalName,
    nint RuntimeHandle,
    long Generation);

public static class AdapterSelector
{
    public static bool Matches(WindowsAdapter adapter, string? stableId, string? friendlyName) =>
        (stableId is null || string.Equals(adapter.StableId, stableId, StringComparison.OrdinalIgnoreCase)) &&
        (friendlyName is null || string.Equals(adapter.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase));

    public static bool TryResolve(IEnumerable<WindowsAdapter> adapters, string? stableId, string? friendlyName, out WindowsAdapter? resolved, out string? error)
    {
        var matches = adapters.Where(adapter => Matches(adapter, stableId, friendlyName)).ToArray();
        if (matches.Length == 1)
        {
            resolved = matches[0];
            error = null;
            return true;
        }

        resolved = null;
        error = matches.Length == 0 ? "No adapter matches the configured selector." : "Adapter selector is ambiguous.";
        return false;
    }
}
