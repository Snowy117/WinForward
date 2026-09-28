namespace WinForward.Configuration;

/// <summary>The accepted values of the <c>udpAssociationReuse</c> configuration key.</summary>
public enum UdpAssociationReuseMode
{
    /// <summary>
    /// Share associations and passively detect a server that pins one client source port per
    /// association, falling back to per-flow associations for that server when pinning is detected.
    /// The production default.
    /// </summary>
    Auto,

    /// <summary>One authenticated association serves up to the per-association flow cap.</summary>
    Always,

    /// <summary>One authenticated association per flow: the rollback lever.</summary>
    Off,
}
