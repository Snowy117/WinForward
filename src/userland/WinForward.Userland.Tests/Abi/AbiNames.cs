namespace WinForward.Userland.Tests.Abi;

internal static class AbiNames
{
    /// <summary>把 snake_case 与 PascalCase 折成同一种形式，用于跨语言比较字段/枚举名。</summary>
    internal static string Normalize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        int length = 0;
        foreach (char character in name)
        {
            if (!char.IsLetterOrDigit(character))
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(character);
        }

        return new string(buffer[..length]);
    }

    /// <summary>snake_case → PascalCase（用于把头里的名字映射到 C# 常量名）。</summary>
    internal static string ToPascal(string snakeCase)
    {
        System.Text.StringBuilder builder = new(snakeCase.Length);
        bool upperNext = true;
        foreach (char character in snakeCase)
        {
            if (character == '_')
            {
                upperNext = true;
                continue;
            }

            builder.Append(upperNext ? char.ToUpperInvariant(character) : character);
            upperNext = false;
        }

        return builder.ToString();
    }
}
