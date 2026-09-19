namespace WinForward.Windows;

public sealed record WindowsAdapter(
    string StableId,
    string FriendlyName,
    string InternalName,
    nint RuntimeHandle,
    long Generation);
