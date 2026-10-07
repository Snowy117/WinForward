namespace WinForward.E2E.Cli;

/// <summary>
/// The argument walk both verbs share.
/// </summary>
/// <remarks>
/// <para>One walk, one set of walk-level refusals: a name the verb does not declare is
/// <c>unknown argument '&lt;argument&gt;'</c>, and a declared option that ran out of arguments is
/// <c>missing value for '&lt;name&gt;'</c>. Spelled once here, a fix to either refusal reaches both
/// verbs instead of one.</para>
/// <para>What a name means is not here. The applier a verb passes in owns the semantics: which
/// values it refuses, what it refuses them with, and (the client's case) whether a value that
/// starts with <c>-</c> is a forgotten option rather than a value. Keeping that with the verb is
/// what lets the target accept a <c>--label</c> of <c>--out</c> while the client refuses one
/// (D14.23).</para>
/// </remarks>
internal static class CommandLine
{
    /// <summary>
    /// One verb's option semantics: assign the value, or refuse it with the message a user reads.
    /// <paramref name="name"/> is always a member of the verb's known set.
    /// </summary>
    internal delegate bool TryApply<in TOptions>(TOptions options, string name, string value, out string? error);

    /// <summary>
    /// Walks <paramref name="args"/> once. Both this walk's own refusals and the applier's land on
    /// <paramref name="error"/>, so a caller reads one channel however the command line was
    /// rejected.
    /// </summary>
#pragma warning disable RCS1239 // Every flag may consume the next argument as its value, so the body advances the index and S127 (error) forbids a for loop here.
    internal static bool TryParse<TOptions>(
        string[] args,
        string[] knownOptions,
        TOptions options,
        TryApply<TOptions> apply,
        out string? error)
    {
        error = null;

        var index = 0;
        while (index < args.Length)
        {
            var argument = args[index];
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator >= 0 ? argument[..separator] : argument;
            var value = separator >= 0 ? argument[(separator + 1)..] : null;

            if (Array.IndexOf(knownOptions, name) < 0)
            {
                error = $"unknown argument '{argument}'";
                return false;
            }

            if (value is null)
            {
                if (++index >= args.Length)
                {
                    error = $"missing value for '{name}'";
                    return false;
                }

                value = args[index];
            }

            if (!apply(options, name, value, out error))
            {
                return false;
            }

            index++;
        }

        return true;
    }
#pragma warning restore RCS1239
}
