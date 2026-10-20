using System.Globalization;

namespace WinForward.Userland.Tests.Abi;

/// <summary>
/// 解析 C++/C# 源码里的整数字面量表达式：十进制、0x 十六进制、u/l 后缀、+ - * / &lt;&lt; &gt;&gt; | 与括号，
/// 标识符交给调用方解析。只够读 ABI 头里的常量，不是通用求值器。
/// </summary>
internal static class IntegerExpression
{
    internal static long Evaluate(string expression, Func<string, long?> resolveIdentifier)
    {
        Parser parser = new(expression, resolveIdentifier);
        long value = parser.ParseOr();
        parser.SkipWhitespace();
        if (!parser.AtEnd)
        {
            throw new FormatException($"表达式无法完整解析：「{expression}」，停在 {parser.Position}");
        }

        return value;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly Func<string, long?> _resolve;
        private int _position;

        internal Parser(string text, Func<string, long?> resolve)
        {
            _text = text;
            _resolve = resolve;
        }

        internal bool AtEnd => _position >= _text.Length;

        internal int Position => _position;

        internal long ParseOr()
        {
            long value = ParseAdditive();
            while (true)
            {
                SkipWhitespace();
                if (Peek("|") && !Peek("||"))
                {
                    _position++;
                    value |= ParseAdditive();
                    continue;
                }

                return value;
            }
        }

        private long ParseAdditive()
        {
            long value = ParseMultiplicative();
            while (true)
            {
                SkipWhitespace();
                if (Peek("+"))
                {
                    _position++;
                    value += ParseMultiplicative();
                }
                else if (Peek("-"))
                {
                    _position++;
                    value -= ParseMultiplicative();
                }
                else
                {
                    return value;
                }
            }
        }

        private long ParseMultiplicative()
        {
            long value = ParseUnary();
            while (true)
            {
                SkipWhitespace();
                if (Peek("<<"))
                {
                    _position += 2;
                    value <<= (int)ParseUnary();
                }
                else if (Peek(">>"))
                {
                    _position += 2;
                    value >>= (int)ParseUnary();
                }
                else if (Peek("*"))
                {
                    _position++;
                    value *= ParseUnary();
                }
                else if (Peek("/"))
                {
                    _position++;
                    long divisor = ParseUnary();
                    if (divisor == 0)
                    {
                        throw new FormatException("表达式里出现除以零");
                    }

                    value /= divisor;
                }
                else
                {
                    return value;
                }
            }
        }

        private long ParseUnary()
        {
            SkipWhitespace();
            if (Peek("-"))
            {
                _position++;
                return -ParseUnary();
            }

            if (Peek("("))
            {
                _position++;
                long value = ParseOr();
                SkipWhitespace();
                if (!Peek(")"))
                {
                    throw new FormatException($"括号不匹配：「{_text}」");
                }

                _position++;
                return value;
            }

            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            SkipWhitespace();
            int start = _position;
            if (!AtEnd && char.IsDigit(_text[_position]))
            {
                while (!AtEnd && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == 'x' ||
                                  _text[_position] == 'X'))
                {
                    _position++;
                }

                string literal = _text[start.._position];
                while (literal.Length > 0 && "uUlL".Contains(literal[^1], StringComparison.Ordinal))
                {
                    literal = literal[..^1];
                }

                bool hex = literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                return long.Parse(literal[(hex ? 2 : 0)..], hex ? NumberStyles.HexNumber : NumberStyles.None,
                                  CultureInfo.InvariantCulture);
            }

            if (!AtEnd && (char.IsLetter(_text[_position]) || _text[_position] == '_'))
            {
                while (!AtEnd && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_' ||
                                  _text[_position] == ':'))
                {
                    _position++;
                }

                string identifier = _text[start.._position];
                long? value = _resolve(identifier);
                return value ?? throw new FormatException($"无法解析标识符「{identifier}」");
            }

            throw new FormatException($"表达式里出现无法识别的内容：「{_text[_position..]}」");
        }

        internal void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }

        private bool Peek(string token) => string.CompareOrdinal(_text, _position, token, 0, token.Length) == 0;
    }
}
