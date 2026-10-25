namespace WinForward.Userland.Tests.Abi;

internal sealed class AbiStructField
{
    internal required string Name { get; init; }

    internal required string TypeName { get; init; }

    internal required int ElementSize { get; init; }

    internal required int Count { get; init; }

    internal required long Offset { get; init; }

    internal int TotalSize => ElementSize * Count;

    internal string NormalizedName => AbiNames.Normalize(Name);

    public override string ToString() => $"{TypeName} {Name}[{Count}] @{Offset}";
}

internal sealed class AbiStruct
{
    internal required string Name { get; init; }

    internal required IReadOnlyList<AbiStructField> Fields { get; init; }

    internal required int Size { get; init; }

    internal AbiStructField? FindField(string name)
    {
        string normalized = AbiNames.Normalize(name);
        return Fields.FirstOrDefault(field => field.NormalizedName == normalized);
    }

    public override string ToString() => $"struct {Name} ({Size} bytes)";
}
