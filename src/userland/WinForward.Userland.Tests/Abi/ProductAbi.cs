using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinForward.Userland.Device;

namespace WinForward.Userland.Tests.Abi;

/// <summary>
/// 用反射与 <see cref="Marshal"/> 描述产品侧 ABI 结构的真实布局，名字按 C++ 头的结构名。
/// </summary>
internal static class ProductAbi
{
    internal static IReadOnlyDictionary<string, Type> Structs { get; } = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["probe_config"] = typeof(ProbeConfig),
        ["probe_stats"] = typeof(ProbeStats),
        ["probe_rule_info"] = typeof(ProbeRuleInfo),
        ["probe_event"] = typeof(ProbeEvent),
        ["process_path_measurement"] = typeof(ProcessPathMeasurement),
        ["redirect_context"] = typeof(RedirectContext),
        ["probe_forwarded_info"] = typeof(ProbeForwardedInfo),
        ["probe_forwarded_stats"] = typeof(ProbeForwardedStats),
    };

    internal static AbiStruct Describe(string headerName)
    {
        Type type = Structs[headerName];
        List<AbiStructField> fields = new();

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            InlineArrayAttribute? inline = field.FieldType.GetCustomAttribute<InlineArrayAttribute>();
            int elementSize;
            int count;
            if (inline is not null)
            {
                FieldInfo element = field.FieldType
                                         .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                         .Single();
                elementSize = Marshal.SizeOf(element.FieldType);
                count = inline.Length;
            }
            else
            {
                elementSize = Marshal.SizeOf(field.FieldType);
                count = 1;
            }

            fields.Add(new AbiStructField
            {
                Name = field.Name,
                TypeName = field.FieldType.Name,
                ElementSize = elementSize,
                Count = count,
                Offset = (long)Marshal.OffsetOf(type, field.Name),
            });
        }

        fields.Sort((left, right) => left.Offset.CompareTo(right.Offset));
        return new AbiStruct { Name = headerName, Fields = fields, Size = Size(headerName) };
    }

    internal static int Size(string headerName)
    {
        Type type = Structs[headerName];
        int marshalled = Marshal.SizeOf(type);
        int managed = (int)(typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(type)
                                .Invoke(null, null) ?? throw new InvalidOperationException($"Unsafe.SizeOf<{type.Name}> 取不到"));
        Check.Equal(marshalled, managed, $"{type.Name} 的 Marshal.SizeOf 与 Unsafe.SizeOf");
        return marshalled;
    }
}
