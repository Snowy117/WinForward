using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class CompilerLimitTests
{
    internal static void Register()
    {
        TestRunner.Add("compiler-limit/blob 总大小 64 KiB 边界", BlobSizeBoundary);
    }

    private static void BlobSizeBoundary()
    {
        const int selectorBytes = 500;

        byte[] accepted = RuleBlobCompiler.Compile(
            LongPathTable(128, selectorBytes), LongPathNormalizer(selectorBytes));
        Check.Equal(128U, BlobReader.Rule(accepted, 0).ProcessCount, "128 条 500 字节选择器");
        Check.True(accepted.Length <= PolicyLimits.RuleBlobMaxSize,
                   $"接受时 blob 长度 {accepted.Length} 不超过 64 KiB");

        PolicyTable rejected = LongPathTable(129, selectorBytes);
        Check.ThrowsContaining<PolicyCompileException>(
            () => RuleBlobCompiler.Compile(rejected, LongPathNormalizer(selectorBytes)),
            "64 KiB", "129 条选择器越过 64 KiB 上限");
    }

    private static PolicyTable LongPathTable(int count, int bytes) => new(
        new[]
        {
            new PolicyRule
            {
                Processes = Enumerable.Range(0, count)
                    .Select(index => ProcessSelector.Parse($@"c:\p{index}.exe"))
                    .ToArray(),
            },
        },
        RuleAction.Permit);

    private static WindowsProcessPathNormalizer LongPathNormalizer(int bytes) => new(path =>
    {
        int begin = path.IndexOf('p', StringComparison.Ordinal) + 1;
        int end = path.IndexOf('.', begin);
        int index = int.Parse(path[begin..end], System.Globalization.CultureInfo.InvariantCulture);
        string number = index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        return new ProcessPathResolution(@"\device\" + number + new string('a', bytes - 8 - number.Length), false);
    });
}
