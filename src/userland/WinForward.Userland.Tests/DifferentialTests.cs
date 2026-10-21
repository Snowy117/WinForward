using WinForward.Userland.Policy;

namespace WinForward.Userland.Tests;

internal static class DifferentialTests
{
    internal const int ExpectedCaseCount = 33;
    internal const int ExpectedConnectionCount = 15;

    internal static void Register()
    {
        TestRunner.Add("differential/用例表精确覆盖 33 组 × 15 条连接", VerifyCoverage);
        TestRunner.Add("differential/夹具落盘且与模型自洽", WriteFixtures);
    }

    private static void VerifyCoverage()
    {
        IReadOnlyList<DifferentialCase> cases = DifferentialCases.All;
        Check.Equal(ExpectedCaseCount, cases.Count, "用例数");

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (DifferentialCase item in cases)
        {
            Check.True(names.Add(item.Name), $"用例名重复：{item.Name}");
            Check.True(IsSlug(item.Name), $"用例名只允许小写字母、数字与连字符：{item.Name}");
            Check.True(IsSingleLine(item.Description), $"{item.Name} 的描述为空或含制表符与换行");
            Check.Equal(ExpectedConnectionCount, item.Connections.Count, $"{item.Name} 的连接数");
            Check.Equal(item.Connections.Count, item.Expectations.Count, $"{item.Name} 的期望条数");

            for (int index = 0; index < item.Connections.Count; index++)
            {
                VerifyConnection(item, index, item.Connections[index]);
            }

            for (int index = 0; index < item.Expectations.Count; index++)
            {
                DifferentialExpectation expected = item.Expectations[index];
                if (!expected.Matched)
                {
                    Check.Equal(0xFFFFFFFFU, expected.RuleIndex, $"{item.Name} 连接 {index} 未命中时的 rule_index");
                    continue;
                }

                Check.True(
                    expected.RuleIndex < item.Table.Rules.Count,
                    $"{item.Name} 连接 {index} 期望命中规则 {expected.RuleIndex}，但表里只有 {item.Table.Rules.Count} 条");
            }
        }
    }

    private static void VerifyConnection(DifferentialCase item, int index, DifferentialConnection connection)
    {
        int expectedLength = connection.Family == RuleAddressFamily.Ipv4 ? 8 : 32;
        Check.Equal(expectedLength, connection.AddressHex.Length, $"{item.Name} 连接 {index} 的地址长度");
        Check.True(
            connection.AddressHex.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"{item.Name} 连接 {index} 的地址不是小写十六进制");
        Check.True(IsSingleLine(connection.Label), $"{item.Name} 连接 {index} 的标签为空或含制表符与换行");
        if (connection.ProcessPath is { } processPath)
        {
            Check.True(
                IsSingleLine(processPath),
                $"{item.Name} 连接 {index} 的进程路径含制表符或换行，会把清单撕成两行");
        }
    }

    private static void WriteFixtures()
    {
        string directory = DifferentialFixtureWriter.ResolveDirectory(null);
        int count = DifferentialFixtureWriter.Write(directory, DifferentialCases.All);
        Console.WriteLine($"[fixtures] {count} 个用例写入 {directory}");

        string manifestPath = Path.Combine(directory, DifferentialFixtureWriter.ManifestFileName);
        Check.True(File.Exists(manifestPath), $"夹具清单没有写出来：{manifestPath}");

        string[] lines = File.ReadAllLines(manifestPath);
        Check.Equal("version\t1", lines[0], "清单版本行");
        Check.Equal(
            count,
            lines.Count(line => line.StartsWith("case\t", StringComparison.Ordinal)),
            "清单里的用例条数");
        Check.Equal(
            count * ExpectedConnectionCount,
            lines.Count(line => line.StartsWith("connect\t", StringComparison.Ordinal)),
            "清单里的连接条数");

        List<(string File, string Name)> blobs = new();
        string currentName = string.Empty;
        foreach (string line in lines)
        {
            if (line.StartsWith("case\t", StringComparison.Ordinal))
            {
                string[] fields = line.Split('\t');
                Check.True(fields.Length >= 2, "case 行缺少用例名");
                currentName = fields[1];
            }
            else if (line.StartsWith("blob\t", StringComparison.Ordinal))
            {
                Check.True(currentName.Length > 0, "blob 行出现在任何 case 行之前");
                blobs.Add((line["blob\t".Length..], currentName));
            }
        }

        Check.Equal(count, blobs.Count, "清单里的 blob 条数");
        foreach ((string file, string name) in blobs)
        {
            string path = Path.Combine(directory, file);
            Check.True(File.Exists(path), $"{name} 的 blob 文件缺失：{path}");

            byte[] blob = File.ReadAllBytes(path);
            RuleBlobHeader header = BlobReader.Header(blob);
            Check.Equal(blob.Length, (int)header.TotalSize, $"{file} 的 total_size");
            Check.Equal(0x31425257U, header.Magic, $"{file} 的 magic");
            Check.Equal((ushort)40, header.HeaderSize, $"{file} 的 header_size");
        }
    }

    private static bool IsSingleLine(string text) =>
        text.Length > 0 &&
        !text.Contains('\t', StringComparison.Ordinal) &&
        !text.Contains('\n', StringComparison.Ordinal) &&
        !text.Contains('\r', StringComparison.Ordinal);

    private static bool IsSlug(string text) =>
        text.Length > 0 && text.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
