using System.Text.RegularExpressions;

namespace WinForward.Userland.Tests.Abi;

/// <summary>
/// 从 <c>src/driver/probe_ioctl.h</c> 与 <c>src/common/redirect_context.h</c> 的文本里读出 ABI：
/// 常量、IOCTL 功能号、结构体字段与布局、枚举取值、<c>static_assert</c> 的尺寸/偏移断言。
/// </summary>
/// <remarks>
/// 只解析这两个头的写法（固定、简单），解析不了直接抛——头文件一旦引入新语法，测试先红，
/// 而不是悄悄跳过某条断言。
/// </remarks>
internal sealed class CppAbiSource
{
    private static readonly Regex ConstantPattern = new(
        @"inline\s+constexpr\s+[\w:<>]+\s+(\w+)\s*=\s*([^;]+);", RegexOptions.Compiled);

    private static readonly Regex IoctlFunctionPattern = new(
        @"inline\s+constexpr\s+std::uint32_t\s+ioctl_(\w+)\s*=\s*make_ioctl\(\s*(0x[0-9A-Fa-f]+)\s*\);",
        RegexOptions.Compiled);

    private static readonly Regex MakeIoctlPartPattern = new(
        @"constexpr\s+std::uint32_t\s+(file_device_unknown|method_buffered|file_any_access)\s*=\s*([^;]+);",
        RegexOptions.Compiled);

    private static readonly Regex StructPattern = new(
        @"struct\s+(\w+)\s*\{(.*?)\n\};", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex FieldPattern = new(
        @"^([A-Za-z_][\w:]*)\s+(\w+)\s*(?:\[\s*([^\]]+)\s*\])?\s*;$", RegexOptions.Compiled);

    private static readonly Regex EnumPattern = new(
        @"enum\s+class\s+(\w+)\s*:\s*[\w:]+\s*\{(.*?)\};", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex EnumDeclarationPattern = new(
        @"enum\s+class\s+(\w+)\s*:\s*([\w:]+)\s*\{", RegexOptions.Compiled);
    private static readonly Regex SizeAssertPattern = new(
        @"static_assert\s*\(\s*sizeof\s*\(\s*(\w+)\s*\)\s*==\s*(\d+)", RegexOptions.Compiled);

    private static readonly Regex OffsetAssertPattern = new(
        @"static_assert\s*\(\s*offsetof\s*\(\s*(\w+)\s*,\s*(\w+)\s*\)\s*==\s*(\d+)", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _rawConstants;

    private readonly Dictionary<string, int> _enumSizes = new(StringComparer.Ordinal);

    private CppAbiSource(string root,
                         Dictionary<string, string> rawConstants,
                         Dictionary<string, AbiStruct> structs,
                         Dictionary<string, IReadOnlyList<(string Name, long Value)>> enums,
                         Dictionary<string, long> ioctlFunctions,
                         long fileDeviceUnknown,
                         long methodBuffered,
                         long fileAnyAccess,
                         Dictionary<(string Struct, string Field), long> sizeAsserts,
                         Dictionary<(string Struct, string Field), long> offsetAsserts)
    {
        Root = root;
        _rawConstants = rawConstants;
        Structs = structs;
        Enums = enums;
        IoctlFunctions = ioctlFunctions;
        FileDeviceUnknown = fileDeviceUnknown;
        MethodBuffered = methodBuffered;
        FileAnyAccess = fileAnyAccess;
        SizeAsserts = sizeAsserts;
        OffsetAsserts = offsetAsserts;
    }

    internal string Root { get; }

    internal Dictionary<string, AbiStruct> Structs { get; }

    internal Dictionary<string, IReadOnlyList<(string Name, long Value)>> Enums { get; }

    internal IReadOnlyDictionary<string, long> IoctlFunctions { get; }

    internal long FileDeviceUnknown { get; }

    internal long MethodBuffered { get; }

    internal long FileAnyAccess { get; }

    /// <summary>键为 (结构名, "")：头里 <c>static_assert(sizeof(x) == n)</c> 的 n。</summary>
    internal IReadOnlyDictionary<(string Struct, string Field), long> SizeAsserts { get; }

    internal IReadOnlyDictionary<(string Struct, string Field), long> OffsetAsserts { get; }

    internal static CppAbiSource Load() => Load(RepositoryRoot.Locate());

    internal static CppAbiSource Load(string root)
    {
        string text = ReadWithoutComments(Path.Combine(root, "src", "driver", "probe_ioctl.h")) + "\n" +
                      ReadWithoutComments(Path.Combine(root, "src", "common", "redirect_context.h"));

        Dictionary<string, string> rawConstants = new(StringComparer.Ordinal);
        foreach (Match match in ConstantPattern.Matches(text))
        {
            if (match.Groups[2].Value.TrimStart().StartsWith("make_ioctl", StringComparison.Ordinal))
            {
                continue;
            }

            rawConstants[match.Groups[1].Value] = match.Groups[2].Value;
        }

        Dictionary<(string, string), long> sizeAsserts = new();
        foreach (Match match in SizeAssertPattern.Matches(text))
        {
            sizeAsserts[(match.Groups[1].Value, string.Empty)] = long.Parse(match.Groups[2].Value);
        }

        Dictionary<(string, string), long> offsetAsserts = new();
        foreach (Match match in OffsetAssertPattern.Matches(text))
        {
            offsetAsserts[(match.Groups[1].Value, match.Groups[2].Value)] = long.Parse(match.Groups[3].Value);
        }

        Dictionary<string, long> ioctlFunctions = new(StringComparer.Ordinal);
        foreach (Match match in IoctlFunctionPattern.Matches(text))
        {
            ioctlFunctions[match.Groups[1].Value] = Convert.ToInt64(match.Groups[2].Value[2..], 16);
        }

        long device = 0;
        long method = 0;
        long access = 0;
        foreach (Match match in MakeIoctlPartPattern.Matches(text))
        {
            long value = IntegerExpression.Evaluate(match.Groups[2].Value, _ => null);
            switch (match.Groups[1].Value)
            {
                case "file_device_unknown":
                    device = value;
                    break;
                case "method_buffered":
                    method = value;
                    break;
                default:
                    access = value;
                    break;
            }
        }

        if (device == 0 || ioctlFunctions.Count == 0)
        {
            throw new InvalidOperationException("probe_ioctl.h 里没有读到了 make_ioctl 的常量或 IOCTL 功能号");
        }

        CppAbiSource source = new(root, rawConstants, new Dictionary<string, AbiStruct>(StringComparer.Ordinal),
                                  new Dictionary<string, IReadOnlyList<(string, long)>>(StringComparer.Ordinal),
                                  ioctlFunctions, device, method, access, sizeAsserts, offsetAsserts);
        source.ParseEnumSizes(text);
        source.ParseStructs(text);
        source.ParseEnums(text);
        return source;
    }

    /// <summary>常量求值（支持常量引用）。不存在时返回 null。</summary>
    internal long? Resolve(string name)
    {
        if (!_rawConstants.TryGetValue(name, out string? expression))
        {
            return null;
        }

        return IntegerExpression.Evaluate(expression, identifier => identifier == name ? null : Resolve(identifier));
    }

    private void ParseStructs(string text)
    {
        foreach (Match match in StructPattern.Matches(text))
        {
            string name = match.Groups[1].Value;
            Structs[name] = BuildStruct(name, match.Groups[2].Value);
        }
    }

    private void ParseEnums(string text)
    {
        foreach (Match match in EnumPattern.Matches(text))
        {
            string name = match.Groups[1].Value;
            List<(string Name, long Value)> values = new();
            long next = 0;
            foreach (string entry in match.Groups[2].Value.Split(','))
            {
                string trimmed = entry.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                int equals = trimmed.IndexOf('=');
                string entryName = equals < 0 ? trimmed : trimmed[..equals].Trim();
                if (equals >= 0)
                {
                    next = IntegerExpression.Evaluate(trimmed[(equals + 1)..], Resolve);
                }

                values.Add((entryName, next));
                next++;
            }

            Enums[name] = values;
        }
    }

    private AbiStruct BuildStruct(string name, string body)
    {
        List<AbiStructField> fields = new();
        long offset = 0;
        int maxAlignment = 1;

        foreach (string rawLine in body.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            Match match = FieldPattern.Match(line);
            if (!match.Success)
            {
                throw new InvalidOperationException($"无法解析 {name} 的字段定义：「{line}」");
            }

            string typeName = match.Groups[1].Value;
            int elementSize = SizeOfCppType(typeName);
            int count = 1;
            if (match.Groups[3].Success)
            {
                count = (int)IntegerExpression.Evaluate(match.Groups[3].Value, Resolve);
            }

            offset = Align(offset, elementSize);
            fields.Add(new AbiStructField
            {
                Name = match.Groups[2].Value,
                TypeName = typeName,
                ElementSize = elementSize,
                Count = count,
                Offset = offset,
            });
            offset += (long)elementSize * count;
            maxAlignment = Math.Max(maxAlignment, elementSize);
        }

        return new AbiStruct { Name = name, Fields = fields, Size = (int)Align(offset, maxAlignment) };
    }

    private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private int SizeOfCppType(string typeName)
    {
        if (_enumSizes.TryGetValue(typeName, out int enumSize))
        {
            return enumSize;
        }

        return typeName switch
        {
            "std::uint8_t" or "char" or "std::int8_t" => 1,
            "std::uint16_t" or "std::int16_t" => 2,
            "std::uint32_t" or "std::int32_t" => 4,
            "std::uint64_t" or "std::int64_t" => 8,
            _ => throw new InvalidOperationException($"ABI 头里出现了测试还不认识的字段类型：{typeName}"),
        };
    }

    private void ParseEnumSizes(string text)
    {
        foreach (Match match in EnumDeclarationPattern.Matches(text))
        {
            _enumSizes[match.Groups[1].Value] = SizeOfCppType(match.Groups[2].Value);
        }
    }


    private static string ReadWithoutComments(string path)
    {
        string text = File.ReadAllText(path);
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            int comment = lines[index].IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                lines[index] = lines[index][..comment];
            }
        }

        return string.Join('\n', lines);
    }
}
