using System.Text.Json;

namespace WinForward.E2E.Analysis.Model;

/// <summary>One effective configuration file a row published.</summary>
/// <param name="Name">The file's name inside the row directory.</param>
/// <param name="Digest">The first twelve hex digits of its SHA-256, or <c>unreadable</c>.</param>
/// <param name="Bytes">Its size, or zero when it could not be read.</param>
internal sealed record ConfigFile(string Name, string Digest, long Bytes);

/// <summary>
/// One ledger record: its parsed payload, the UTC instant its envelope carries, and the running delta the
/// datagram views derive from it.
/// </summary>
/// <param name="LedgerPath">The ledger file the record came from.</param>
/// <param name="Payload">The line's JSON object.</param>
/// <param name="Utc">The instant the record's own <c>utc</c> member names, or null when it carries none.</param>
/// <param name="ReceivedDelta">The datagrams received since the previous summary of the same ledger.</param>
internal sealed record LedgerRecord(string LedgerPath, JsonElement Payload, DateTimeOffset? Utc, double ReceivedDelta);

/// <summary>
/// One target ledger: the file it was read from, its records in file order, and the lines that were not a
/// JSON object.
/// </summary>
/// <param name="Path">The ledger's path, as discovered; it is part of §2's text and §14's rows.</param>
/// <param name="Records">One entry per non-blank line that parsed as a JSON object.</param>
/// <param name="BadLines">Lines that were not a JSON object; counted rather than thrown on.</param>
/// <param name="LoadErrors">Why the file could not be read, when it could not.</param>
internal sealed record LedgerData(
    string Path,
    IReadOnlyList<LedgerRecord> Records,
    int BadLines,
    IReadOnlyList<string> LoadErrors);
