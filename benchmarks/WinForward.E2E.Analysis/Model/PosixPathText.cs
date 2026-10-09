namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// The text a <see cref="System.IO.Path"/>-like argument is printed as: the reference turns
/// <c>--raw</c> into a <c>Path</c> and publishes <c>str(path)</c> in <c>verdict.json</c> and in
/// <c>tables.md</c>, so the spelling of the argument is part of the compared bytes.
/// </summary>
/// <remarks>
/// <para><b>Normalization, not resolution.</b> A <c>Path</c> collapses repeated separators, drops
/// <c>.</c> components and strips a trailing separator; it never resolves <c>..</c> and never makes a
/// relative path absolute, which is what keeps the ledger paths §2 prints comparable.</para>
/// <para><b>Only POSIX separators are handled.</b> Both implementations read the same trees with the
/// same slash-separated arguments; a backslash is an ordinary character in a POSIX path and is left
/// where it is.</para>
/// </remarks>
internal static class PosixPathText
{
    /// <summary>The separator of the path syntax both implementations are called with.</summary>
    private const char Separator = '/';

    /// <summary>The text <c>str(Path(...))</c> produces for <paramref name="path"/>.</summary>
    internal static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // A leading double slash is a POSIX implementation-defined root and is kept as one.
        var absolute = path.StartsWith(Separator);
        var keepDoubleSlash = path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("///", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (var part in path.Split(Separator))
        {
            if (part.Length == 0 || string.Equals(part, ".", StringComparison.Ordinal))
            {
                continue;
            }

            parts.Add(part);
        }

        if (parts.Count == 0)
        {
            return absolute ? "/" : ".";
        }

        var text = string.Join(Separator, parts);
        if (!absolute)
        {
            return text;
        }

        return keepDoubleSlash ? "//" + text : "/" + text;
    }

    /// <summary>
    /// The parent directory's text, the way <c>Path.parent</c> answers it: a lexical answer, so
    /// <c>..</c> is never resolved and a relative path stays relative.
    /// </summary>
    internal static string Parent(string path)
    {
        var text = Normalize(path);
        if (text.All(character => character == Separator))
        {
            return text;
        }

        var slash = text.LastIndexOf(Separator);
        if (slash < 0)
        {
            return ".";
        }

        return text[0] == Separator && slash <= 1 ? text[..(slash + 1)] : text[..slash];
    }

    /// <summary>
    /// One name inside a directory, the way <c>Path / name</c> spells it: an absolute name replaces
    /// the directory, and a <c>.</c> directory adds no prefix.
    /// </summary>
    internal static string Join(string directory, string name)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(name);

        if (name.StartsWith(Separator))
        {
            return Normalize(name);
        }

        var parent = Normalize(directory);
        return string.Equals(parent, ".", StringComparison.Ordinal) ? Normalize(name) : Normalize(parent + Separator + name);
    }
}
