using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal readonly record struct DifferentialExpectation(bool Matched, uint RuleIndex, RuleAction Action);
