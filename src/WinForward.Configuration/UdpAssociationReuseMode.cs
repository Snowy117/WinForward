namespace WinForward.Configuration;

/// <summary>The accepted values of the <c>udpAssociationReuse</c> configuration key.</summary>
public enum UdpAssociationReuseMode
{
    /// <summary>Sharing with passive capability detection (Step 3); currently off-equivalent in the pool.</summary>
    Auto,

    /// <summary>One authenticated association serves up to the per-association flow cap.</summary>
    Always,

    /// <summary>One authenticated association per flow: the rollback lever.</summary>
    Off,
}
