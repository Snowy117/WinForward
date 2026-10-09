namespace WinForward.E2E.Analysis.Model;

/// <summary>
/// One value the analysis read out of a run, or the reason it could not be read.
/// </summary>
/// <remarks>
/// <para><b>Either the value or the reason, never a default.</b> A null <see cref="Value"/> always
/// carries the <see cref="Reason"/> that the table cell turns into <c>n/a (reason)</c>, so a metric
/// that cannot be computed says why instead of reporting a zero nobody measured. A null value with a
/// null reason is not a state any producer writes.</para>
/// <para><b>The type argument is the value's own type.</b> A reader that may find nothing hands back
/// <c>Measured&lt;double?&gt;</c> or <c>Measured&lt;JsonElement?&gt;</c>: the nullability belongs to the
/// reading, so a caller that logs <see cref="Value"/> sees the same type the reader produced.</para>
/// </remarks>
internal readonly record struct Measured<T>(T Value, string? Reason);
