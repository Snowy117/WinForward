namespace WinForward.Userland.Tests;

internal static class DifferentialCases
{
    internal static IReadOnlyList<DifferentialCase> All { get; } =
        PriorityCases.All.Concat(NetworkCases.All).Concat(TransportCases.All).Concat(ProcessCases.All).ToArray();
}
