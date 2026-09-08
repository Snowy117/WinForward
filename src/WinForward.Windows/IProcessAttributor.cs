using WinForward.Core;

namespace WinForward.Windows;

public readonly record struct ProcessIdentity(uint ProcessId, DateTime CreationTimeUtc, string? Name, string? FullPath);

public interface IProcessAttributor
{
    ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken);
}

public sealed class UnsupportedProcessAttributor : IProcessAttributor
{
    public ValueTask<ProcessIdentity?> FindAsync(FlowKey key, CancellationToken cancellationToken) => ValueTask.FromResult<ProcessIdentity?>(null);
}
