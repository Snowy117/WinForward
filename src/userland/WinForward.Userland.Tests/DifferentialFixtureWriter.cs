using System.Globalization;
using System.Text;
using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class DifferentialFixtureWriter
{
    internal const string EnvironmentVariable = "WF_DIFF_FIXTURE_DIR";
    internal const string ManifestFileName = "manifest.txt";
    internal const int ManifestVersion = 1;

    internal static string ResolveDirectory(string? overrideDirectory)
    {
        if (!string.IsNullOrEmpty(overrideDirectory))
        {
            return Path.GetFullPath(overrideDirectory);
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return Path.Combine(FindRepositoryRoot(), "build", "diff-fixtures");
    }

    internal static int Write(string directory, IReadOnlyList<DifferentialCase> cases)
    {
        Directory.CreateDirectory(directory);
        foreach (string stale in Directory.EnumerateFiles(directory, "*.blob"))
        {
            File.Delete(stale);
        }

        string manifestPath = Path.Combine(directory, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        StringBuilder manifest = new();
        manifest.Append("version\t").Append(ManifestVersion).Append('\n');

        foreach (DifferentialCase item in cases)
        {
            string fileName = item.Name + ".blob";
            byte[] blob;
            try
            {
                blob = TestAppId.Compile(item.Table);
            }
            catch (PolicyCompileException exception)
            {
                throw new TestFailure($"用例 {item.Name} 的规则表无法编译：{exception.Message}");
            }

            File.WriteAllBytes(Path.Combine(directory, fileName), blob);


            manifest.Append("case\t").Append(item.Name).Append('\t').Append(item.Description).Append('\n');
            manifest.Append("blob\t").Append(fileName).Append('\n');
            manifest.Append("rules\t").Append(item.Table.Rules.Count).Append('\n');
            manifest.Append("fallback\t").Append(ActionName(item.Table.FallbackAction)).Append('\n');

            for (int index = 0; index < item.Connections.Count; index++)
            {
                DifferentialConnection connection = item.Connections[index];
                DifferentialExpectation expected = item.Expectations[index];
                manifest
                    .Append("connect\t")
                    .Append(connection.AddressHex).Append('\t')
                    .Append(connection.Port).Append('\t')
                    .Append(connection.Protocol).Append('\t')
                    .Append(FamilyName(connection.Family)).Append('\t')
                    .Append(connection.ProcessPath ?? "-").Append('\t')
                    .Append(expected.Matched ? '1' : '0').Append('\t')
                    .Append(expected.Matched ? expected.RuleIndex.ToString(CultureInfo.InvariantCulture) : "-").Append('\t')
                    .Append(ActionName(expected.Action)).Append('\t')
                    .Append(connection.Label).Append('\n');
            }
        }

        File.WriteAllText(manifestPath, manifest.ToString(), new UTF8Encoding(false));
        return cases.Count;
    }

    internal static string ActionName(RuleAction action) => action switch
    {
        RuleAction.Permit => "permit",
        RuleAction.Block => "block",
        RuleAction.Redirect => "redirect",
        _ => throw new TestFailure($"未知动作 {(byte)action}"),
    };

    internal static string FamilyName(RuleAddressFamily family) => family switch
    {
        RuleAddressFamily.Ipv4 => "ipv4",
        RuleAddressFamily.Ipv6 => "ipv6",
        _ => throw new TestFailure($"连接事实不能是 family={family}"),
    };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "common", "rule_blob.h")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new TestFailure($"从 {AppContext.BaseDirectory} 向上找不到仓库根（src/common/rule_blob.h）");
    }
}
