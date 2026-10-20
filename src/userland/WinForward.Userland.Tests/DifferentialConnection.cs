using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal sealed record DifferentialConnection(
    string Label,
    string AddressHex,
    ushort Port,
    byte Protocol,
    RuleAddressFamily Family,
    string? ProcessPath);
