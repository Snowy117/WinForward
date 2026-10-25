using WinForward.Userland.Policy;

namespace WinForward.Cli.Backends.Kernel;

/// <summary>
/// The <see cref="IProcessPathNormalizer"/> a non-Windows host validates with: a filename selector
/// never reaches it, so a filename-only policy compiles everywhere, while a path selector is refused
/// with the reason instead of being silently compiled into a rule that cannot match.
/// </summary>
/// <remarks>
/// The Kernel blob stores path selectors in the ALE_APP_ID form, which is only obtainable by opening
/// the path with Windows file-handle semantics. Resolving on another host would have to guess a
/// <c>\device\...</c> mapping, and a guessed rule never matches the process it names.
/// </remarks>
internal sealed class UnsupportedProcessPathNormalizer : IProcessPathNormalizer
{
    public ProcessPathResolution Normalize(string path) =>
        throw new PlatformNotSupportedException(
            $@"Kernel process path resolution requires Windows: '{path}' is a path selector, and the host rule blob stores the \device\... form only a Windows handle can produce. A filename selector compiles on any host.");
}
