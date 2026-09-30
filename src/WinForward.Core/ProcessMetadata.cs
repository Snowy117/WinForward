namespace WinForward.Core;

/// <summary>
/// The process identity a host flow was attributed to, interned once per flow at claim time so the
/// per-packet <see cref="FlowContext"/> holds one reference instead of two strings. Warm packets
/// carry no instance at all (the classifier never attributes), exactly as their process fields were
/// null before, and a flow whose attribution found no owner keeps <see langword="null"/> too.
/// </summary>
public sealed record ProcessMetadata(string? ProcessName, string? ProcessPath);
