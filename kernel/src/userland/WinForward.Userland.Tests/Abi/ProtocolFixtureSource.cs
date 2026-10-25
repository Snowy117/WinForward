using System.Text.RegularExpressions;

namespace WinForward.Userland.Tests.Abi;

/// <summary>
/// 从 <c>tools/wfprobe/Protocol.cs</c> 的文本里读出测试夹具的 ABI 定义：
/// 结构体字段、Pack、布局断言的尺寸、常量、IOCTL 功能号与枚举取值。
/// </summary>
/// <remarks>
/// 夹具是产品之外的第二份拷贝。它不引用产品工程（它是独立的测试程序），所以两边的一致性
/// 只能靠这个测试锁住：同一份 C++ 头是权威，产品与夹具都必须与它逐字段对齐。
/// </remarks>
internal sealed class ProtocolFixtureSource
{
    private static readonly Regex StructPattern = new(
        @"\[StructLayout\(LayoutKind\.Sequential,\s*Pack\s*=\s*(\d+)\)\]\s*(?:internal|public)?\s*(?:unsafe\s+)?struct\s+(\w+)\s*\n?\{(.*?)\n\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FieldPattern = new(
        @"public\s+(?:fixed\s+)?([A-Za-z_][\w.]*)\s+(\w+)\s*(?:\[\s*(\d+)\s*\])?\s*;", RegexOptions.Compiled);

    private static readonly Regex ConstantPattern = new(
        @"public\s+const\s+(\w+)\s+(\w+)\s*=\s*([^;]+);", RegexOptions.Compiled);

    private static readonly Regex CodePropertyPattern = new(
        @"public\s+static\s+uint\s+(\w+)Code\s*=>\s*MakeIoctl\((\w+)\);", RegexOptions.Compiled);

    private static readonly Regex LayoutCheckPattern = new(
        @"Check<(\w+)>\((\d+),", RegexOptions.Compiled);

    private static readonly Regex MakeIoctlDevicePattern = new(
        @"0x([0-9A-Fa-f]+)u?\s*<<\s*16", RegexOptions.Compiled);

    private ProtocolFixtureSource(string root)
    {
        Root = root;
    }

    internal string Root { get; }

    internal Dictionary<string, AbiStruct> Structs { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, long> Constants { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, long> LayoutChecks { get; } = new(StringComparer.Ordinal);

    /// <summary>属性名（如 SetConfigCode）→ 它引用的功能号常量名（IoctlSetConfig）。</summary>
    internal Dictionary<string, string> CodeProperties { get; } = new(StringComparer.Ordinal);

    internal long DeviceConstant { get; private set; }

    internal static ProtocolFixtureSource Load(string root)
    {
        string path = Path.Combine(root, "tools", "wfprobe", "Protocol.cs");
        string text = File.ReadAllText(path);
        ProtocolFixtureSource source = new(root);

        foreach (Match match in StructPattern.Matches(text))
        {
            int pack = int.Parse(match.Groups[1].Value);
            source.Structs[match.Groups[2].Value] = BuildStruct(match.Groups[2].Value, match.Groups[3].Value, pack);
        }

        foreach (Match match in ConstantPattern.Matches(text))
        {
            try
            {
                source.Constants[match.Groups[2].Value] =
                    IntegerExpression.Evaluate(match.Groups[3].Value, _ => null);
            }
            catch (FormatException)
            {
                // 非数值常量（PoolTag 之类）不参与 ABI 比较。
            }
        }

        foreach (Match match in LayoutCheckPattern.Matches(text))
        {
            source.LayoutChecks[match.Groups[1].Value] = long.Parse(match.Groups[2].Value);
        }

        foreach (Match match in CodePropertyPattern.Matches(text))
        {
            source.CodeProperties[match.Groups[1].Value + "Code"] = match.Groups[2].Value;
        }

        Match device = MakeIoctlDevicePattern.Match(text);
        if (!device.Success)
        {
            throw new InvalidOperationException("Protocol.cs 里读不到 MakeIoctl 的 device 常量");
        }

        source.DeviceConstant = Convert.ToInt64(device.Groups[1].Value, 16);
        return source;
    }

    private static AbiStruct BuildStruct(string name, string body, int pack)
    {
        List<AbiStructField> fields = new();
        long offset = 0;
        int maxAlignment = 1;

        foreach (Match match in FieldPattern.Matches(body))
        {
            string typeName = match.Groups[1].Value;
            int elementSize = SizeOfCSharpType(typeName);
            int count = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 1;

            int alignment = pack == 0 ? elementSize : Math.Min(elementSize, pack);
            offset = (offset + alignment - 1) / alignment * alignment;
            fields.Add(new AbiStructField
            {
                Name = match.Groups[2].Value,
                TypeName = typeName,
                ElementSize = elementSize,
                Count = count,
                Offset = offset,
            });
            offset += (long)elementSize * count;
            maxAlignment = Math.Max(maxAlignment, alignment);
        }

        int structAlignment = pack == 0 ? maxAlignment : Math.Min(maxAlignment, pack);
        int size = (int)((offset + structAlignment - 1) / structAlignment * structAlignment);
        return new AbiStruct { Name = name, Fields = fields, Size = size };
    }

    private static int SizeOfCSharpType(string typeName) => typeName switch
    {
        "byte" or "sbyte" or "bool" => 1,
        "ushort" or "short" or "char" => 2,
        "uint" or "int" or "float" => 4,
        "ulong" or "long" or "double" => 8,
        _ => throw new InvalidOperationException($"协议夹具里出现了测试还不认识的字段类型：{typeName}"),
    };
}
