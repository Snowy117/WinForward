using System.Globalization;
using System.Reflection;
using WinForward.Userland.Device;

namespace WinForward.Userland.Tests.Abi;

/// <summary>
/// 把三份 ABI 定义锁在一起：<c>src/driver/probe_ioctl.h</c>（权威）、产品的 <c>Device/</c>、
/// <c>tools/wfprobe/Protocol.cs</c>（测试夹具）。
/// </summary>
/// <remarks>
/// 产品与夹具都不引用彼此；能防止它们各自漂移的只有这组测试：它读 C++ 头的文本，
/// 同时用反射读产品结构的真实布局、用文本解析读夹具的定义，逐字段比较。
/// 头文件本身的 <c>static_assert</c> 也先与扫描出的布局对一遍，避免解析器悄悄算错。
/// </remarks>
internal static class AbiConsistencyTests
{
    private static readonly string[] ProbeStructNames =
    {
        "probe_config",
        "probe_stats",
        "probe_rule_info",
        "probe_event",
        "probe_forwarded_info",
        "probe_forwarded_stats",
        "probe_forwarded_config",
        "probe_forwarded_ingress",
        "probe_forwarded_ingress_config",

        "probe_forwarded_redirect_query",
        "probe_forwarded_redirect",
        "process_path_measurement",
        "redirect_context",
    };

    private static readonly string[] FixtureStructNames =
    {
        "probe_config",
        "probe_stats",
        "probe_rule_info",
        "probe_event",
        "probe_forwarded_info",
        "probe_forwarded_stats",
        "probe_forwarded_config",
        "probe_forwarded_ingress",
        "probe_forwarded_ingress_config",

        "probe_forwarded_redirect_query",
        "probe_forwarded_redirect",
        "redirect_context",
    };

    private static readonly Lazy<CppAbiSource> Header = new(CppAbiSource.Load);

    private static readonly Lazy<ProtocolFixtureSource> Fixture =
        new(() => ProtocolFixtureSource.Load(RepositoryRoot.Locate()));

    internal static void Register()
    {
        TestRunner.Add("abi/头文件布局与自身 static_assert 一致", HeaderSelfCheck);
        TestRunner.Add("abi/产品结构尺寸与头文件一致", ProductSizes);
        TestRunner.Add("abi/产品结构字段与头文件逐项一致", ProductFields);
        TestRunner.Add("abi/产品 IOCTL 常量与头文件一致", ProductIoctlConstants);
        TestRunner.Add("abi/产品枚举取值与头文件一致", ProductEnums);
        TestRunner.Add("abi/产品布局断言常量与头文件一致", ProductLayoutConstants);
        TestRunner.Add("abi/夹具结构尺寸与头文件一致", FixtureSizes);
        TestRunner.Add("abi/夹具结构字段与头文件逐项一致", FixtureFields);
        TestRunner.Add("abi/夹具 IOCTL 常量与头文件一致", FixtureIoctlConstants);
        TestRunner.Add("abi/夹具枚举取值与头文件一致", FixtureEnums);
        TestRunner.Add("abi/redirect_context 常量三处一致", RedirectContextConstants);
    }

    private static void HeaderSelfCheck()
    {
        foreach (string name in ProbeStructNames)
        {
            Check.True(Header.Value.Structs.ContainsKey(name), $"头文件里缺少结构 {name}");
            Check.True(Header.Value.SizeAsserts.ContainsKey((name, string.Empty)), $"{name} 缺少 static_assert(sizeof)");
        }

        foreach (((string structName, string _), long size) in Header.Value.SizeAsserts)
        {
            AbiStruct structure = Header.Value.Structs[structName];
            Check.Equal(size, (long)structure.Size, $"扫描出的 sizeof({structName}) 与 static_assert 不一致");
        }

        foreach (((string structName, string fieldName), long offset) in Header.Value.OffsetAsserts)
        {
            AbiStructField field = Header.Value.Structs[structName].FindField(fieldName)
                                   ?? throw new TestFailure($"{structName} 里没有字段 {fieldName}");
            Check.Equal(offset, field.Offset, $"扫描出的 offsetof({structName}, {fieldName}) 与 static_assert 不一致");
        }
    }

    private static void ProductSizes()
    {
        foreach (string name in ProbeStructNames)
        {
            long expected = Header.Value.SizeAsserts[(name, string.Empty)];
            Check.Equal(expected, (long)ProductAbi.Size(name), $"产品侧 sizeof({name})");
        }
    }

    private static void ProductFields()
    {
        foreach (string name in ProbeStructNames)
        {
            AssertSameLayout(Header.Value.Structs[name], ProductAbi.Describe(name), $"产品 {name}");
        }
    }

    private static void ProductIoctlConstants()
    {
        foreach ((string name, long function) in Header.Value.IoctlFunctions)
        {
            string pascal = AbiNames.ToPascal(name);
            Check.Equal(function, ProductConstant(pascal + "Function"), $"ProbeIoctl.{pascal}Function");
            long expectedCode = ExpectedCode(function);
            Check.Equal(expectedCode, ProductConstant(pascal + "Code"), $"ProbeIoctl.{pascal}Code");
            Check.Equal(expectedCode, (long)ProbeIoctl.MakeIoctl((uint)function), "ProbeIoctl.MakeIoctl 与头文件公式");
        }

        Check.Equal(RequiredConstant("probe_abi_version"), ProductConstant("AbiVersion"), "ProbeIoctl.AbiVersion");
        Check.Equal(RequiredConstant("max_self_process_ids"), ProductConstant("MaxSelfProcessIds"),
                    "ProbeIoctl.MaxSelfProcessIds");
        Check.Equal(RequiredConstant("rule_blob_max_size"), ProductConstant("RuleBlobMaxSize"),
                    "ProbeIoctl.RuleBlobMaxSize");
        Check.Equal(RequiredConstant("process_path_measure_input_max"), ProductConstant("ProcessPathMeasureInputMax"),
                    "ProbeIoctl.ProcessPathMeasureInputMax");
    }

    private static void ProductEnums()
    {
        foreach ((string enumName, IReadOnlyList<(string Name, long Value)> values) in Header.Value.Enums)
        {
            if (enumName is "address_family" or "transport_protocol")
            {
                // redirect_context.h 的两个枚举在产品侧是 RedirectContext 的常量，由 RedirectContextConstants 钉住。
                continue;
            }
            Type type = ProductEnumType(enumName);
            string[] names = Enum.GetNames(type);
            Check.Equal(values.Count, names.Length, $"{type.Name} 的成员数");

            foreach ((string headerName, long headerValue) in values)
            {
                string? member = names.FirstOrDefault(candidate =>
                                                          AbiNames.Normalize(candidate) == AbiNames.Normalize(headerName));
                Check.True(member is not null, $"{type.Name} 缺少成员 {headerName}");
                long actual = Convert.ToInt64(Enum.Parse(type, member!), CultureInfo.InvariantCulture);
                Check.Equal(headerValue, actual, $"{type.Name}.{member}");
            }
        }
    }

    private static void ProductLayoutConstants()
    {
        (string Header, string Field)[] expected =
        {
            ("probe_config", "ProbeConfigSize"),
            ("probe_stats", "ProbeStatsSize"),
            ("probe_rule_info", "ProbeRuleInfoSize"),
            ("probe_event", "ProbeEventSize"),
            ("process_path_measurement", "ProcessPathMeasurementSize"),
            ("redirect_context", "RedirectContextSize"),
            ("probe_forwarded_info", "ProbeForwardedInfoSize"),
            ("probe_forwarded_stats", "ProbeForwardedStatsSize"),
            ("probe_forwarded_config", "ProbeForwardedConfigSize"),
            ("probe_forwarded_ingress", "ProbeForwardedIngressSize"),
            ("probe_forwarded_ingress_config", "ProbeForwardedIngressConfigSize"),

            ("probe_forwarded_redirect_query", "ProbeForwardedRedirectQuerySize"),
            ("probe_forwarded_redirect", "ProbeForwardedRedirectSize"),
        };

        foreach ((string header, string field) in expected)
        {
            FieldInfo constant = typeof(DeviceLayout).GetField(field, BindingFlags.Public | BindingFlags.Static)
                                 ?? throw new TestFailure($"DeviceLayout 缺少常量 {field}");
            Check.Equal((long)Header.Value.SizeAsserts[(header, string.Empty)],
                        Convert.ToInt64(constant.GetRawConstantValue()!), $"DeviceLayout.{field}");
        }
    }

    private static void FixtureSizes()
    {
        foreach (string name in FixtureStructNames)
        {
            AbiStruct fixture = FixtureStruct(name);
            Check.Equal((long)Header.Value.SizeAsserts[(name, string.Empty)], (long)fixture.Size, $"夹具 {fixture.Name}");
            Check.True(Fixture.Value.LayoutChecks.TryGetValue(fixture.Name, out long checkedSize),
                       $"夹具 ProtocolLayout.Verify 没有覆盖 {fixture.Name}");
            Check.Equal((long)Header.Value.SizeAsserts[(name, string.Empty)], checkedSize,
                        $"夹具 Check<{fixture.Name}> 的期望尺寸");
        }
    }

    private static void FixtureFields()
    {
        foreach (string name in FixtureStructNames)
        {
            AssertSameLayout(Header.Value.Structs[name], FixtureStruct(name), $"夹具 {name}");
        }
    }

    private static void FixtureIoctlConstants()
    {
        Check.Equal(Header.Value.FileDeviceUnknown, Fixture.Value.DeviceConstant, "夹具 MakeIoctl 的 device 常量");

        foreach ((string name, long function) in Header.Value.IoctlFunctions)
        {
            string pascal = AbiNames.ToPascal(name);
            string functionField = "Ioctl" + pascal;
            Check.True(Fixture.Value.Constants.TryGetValue(functionField, out long fixtureFunction),
                       $"夹具缺少常量 {functionField}");
            Check.Equal(function, fixtureFunction, $"夹具 {functionField}");

            Check.True(Fixture.Value.CodeProperties.TryGetValue(pascal + "Code", out string? referenced),
                       $"夹具缺少属性 {pascal}Code");
            Check.Equal(functionField, referenced!, $"夹具 {pascal}Code 引用的功能号常量");
        }

        Check.Equal(RequiredConstant("probe_abi_version"), FixtureConstant("AbiVersion"), "夹具 AbiVersion");
        Check.Equal(RequiredConstant("max_self_process_ids"), FixtureConstant("MaxSelfProcessIds"),
                    "夹具 MaxSelfProcessIds");
        Check.Equal(RequiredConstant("rule_blob_max_size"), FixtureConstant("RuleBlobMaxSize"), "夹具 RuleBlobMaxSize");
    }

    private static void FixtureEnums()
    {
        foreach ((string enumName, IReadOnlyList<(string Name, long Value)> values) in Header.Value.Enums)
        {
            string prefix = enumName switch
            {
                "probe_decision" => "Decision",
                "probe_action" => "Action",
                _ => string.Empty,
            };
            if (prefix.Length == 0)
            {
                continue;
            }

            foreach ((string headerName, long headerValue) in values)
            {
                string constantName = prefix + AbiNames.ToPascal(headerName);
                Check.True(Fixture.Value.Constants.ContainsKey(constantName), $"夹具缺少常量 {constantName}");
                Check.Equal(headerValue, FixtureConstant(constantName), $"夹具 {constantName}");
            }
        }
    }

    private static void RedirectContextConstants()
    {
        long magic = RequiredConstant("redirect_context_magic");
        long version = RequiredConstant("redirect_context_version");
        long addressSize = RequiredConstant("address_storage_size");

        Check.Equal(magic, Convert.ToInt64(RedirectContext.MagicValue), "产品 RedirectContext.Magic");
        Check.Equal(version, Convert.ToInt64(RedirectContext.VersionValue), "产品 RedirectContext.Version");
        Check.Equal(addressSize, (long)RedirectContext.AddressSize, "产品 RedirectContext.AddressSize");
        Check.Equal(magic, FixtureConstant("RedirectContextMagic"), "夹具 RedirectContextMagic");
        Check.Equal(version, FixtureConstant("RedirectContextVersion"), "夹具 RedirectContextVersion");

        (string Name, long Value)[] families = Header.Value.Enums["address_family"].ToArray();
        Check.Equal(2, families.Length, "address_family 成员数");
        Check.Equal(families[0].Value, (long)RedirectContext.FamilyIpv4, "RedirectContext.FamilyIpv4");
        Check.Equal(families[1].Value, (long)RedirectContext.FamilyIpv6, "RedirectContext.FamilyIpv6");
        Check.Equal(2, Header.Value.Enums["transport_protocol"].Count, "transport_protocol 成员数");
        Check.Equal(Header.Value.Enums["transport_protocol"][0].Value, (long)RedirectContext.ProtocolTcp,
                    "RedirectContext.ProtocolTcp");
        Check.Equal(Header.Value.Enums["transport_protocol"][1].Value, (long)RedirectContext.ProtocolUdp,
                    "RedirectContext.ProtocolUdp");
    }

    private static void AssertSameLayout(AbiStruct expected, AbiStruct actual, string what)
    {
        Check.Equal(expected.Size, actual.Size, $"{what} 的 sizeof");
        Check.Equal(expected.Fields.Count, actual.Fields.Count, $"{what} 的字段数");

        for (int index = 0; index < Math.Min(expected.Fields.Count, actual.Fields.Count); index++)
        {
            AbiStructField left = expected.Fields[index];
            AbiStructField right = actual.Fields[index];
            Check.Equal(left.NormalizedName, right.NormalizedName, $"{what} 第 {index} 个字段的名字");
            Check.Equal(left.ElementSize, right.ElementSize, $"{what}.{left.Name} 的元素大小");
            Check.Equal(left.Count, right.Count, $"{what}.{left.Name} 的元素个数");
            Check.Equal(left.Offset, right.Offset, $"{what}.{left.Name} 的偏移");
        }
    }

    private static AbiStruct FixtureStruct(string headerName)
    {
        string normalized = AbiNames.Normalize(headerName);
        return Fixture.Value.Structs.Values.FirstOrDefault(structure => AbiNames.Normalize(structure.Name) == normalized)
               ?? throw new TestFailure($"夹具里没有结构 {headerName}");
    }

    private static long ExpectedCode(long function) =>
        (Header.Value.FileDeviceUnknown << 16) | (Header.Value.FileAnyAccess << 14) | (function << 2) |
        Header.Value.MethodBuffered;

    private static long RequiredConstant(string name) =>
        Header.Value.Resolve(name) ?? throw new TestFailure($"头文件里没有常量 {name}");

    private static long ProductConstant(string name)
    {
        FieldInfo field = typeof(ProbeIoctl).GetField(name, BindingFlags.Public | BindingFlags.Static)
                          ?? throw new TestFailure($"ProbeIoctl 缺少常量 {name}");
        return Convert.ToInt64(field.GetRawConstantValue(), CultureInfo.InvariantCulture);
    }

    private static long FixtureConstant(string name) =>
        Fixture.Value.Constants.TryGetValue(name, out long value)
            ? value
            : throw new TestFailure($"夹具缺少常量 {name}");

    private static Type ProductEnumType(string headerEnum) => headerEnum switch
    {
        "probe_decision" => typeof(ProbeDecision),
        "probe_action" => typeof(ProbeAction),
        "redirect_branch" => typeof(RedirectBranch),
        _ => throw new TestFailure($"测试还不认识头文件里的枚举 {headerEnum}"),
    };
}
