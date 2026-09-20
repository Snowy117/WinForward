using WinForward.Core;

namespace WinForward.Windows;

public readonly record struct ProcessIdentity(string? Name, string? FullPath);

public interface IProcessAttributor
{
    ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken);
}
