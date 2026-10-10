# Design — reusable owner-table slots

## Current shape

| Piece | File | Today |
|---|---|---|
| Native scan + parse | `src/WinForward.Windows/IPHelperOwnerTableReader.cs` | one `Marshal.AllocHGlobal` per scan, one fresh `TcpOwnerRow[]`/`UdpOwnerRow[]`, one `IPAddress` per endpoint per row |
| Snapshot + reuse window | `src/WinForward.Windows/ProcessOwnerTableCache.cs` | immutable `OwnerTableSnapshot` per kind; lock-free positive reuse; single-flight read under a per-kind `Lock` |
| Row storage + predicates | `src/WinForward.Windows/IProcessOwnerTableReader.cs` (`OwnerTable`) | immutable arrays handed to the snapshot |
| Seam | `IProcessOwnerTableReader.Read(OwnerTableKind)` | returns a fresh immutable table |

A scan allocates O(rows) managed objects **and** hands them to a snapshot that lives for the reuse
window, so the array and its `IPAddress` objects cannot be returned to anything: the GC collects them
only after the snapshot is replaced.

## Chosen shape

**One reusable, cache-searched slot per `OwnerTableKind`.** The slot owns the row storage; the reader
fills it in place; every lookup searches it while holding that kind's gate. Nothing about *when* a
scan happens, or what it may answer, changes.

```
Lookup(key, requestInstant):
  kind = KindOf(key) or return null
  lock gate[kind]:
      slot = slots[kind]                       # reusable storage, contents = newest scan
      if slot.contents exist and slot.available:
          if slot.takenUtc >= requestInstant:  # contents are not older than the request
              return slot.search(key)          # authoritative: positive or negative
          if reuses(kind) and fresh(slot) and slot.search(key) is hit:
              return hit                       # TCP-only reuse window
      read(slot)                               # refill in place; unavailable => no publish
      return slot.search(key)
```

Where "contents are not older than the request" is sound: `takenUtc` is sampled *after* the native
call returns, and the read sees every socket that existed when it ran.

### Why the gate now covers the search

The old design could search without a lock because each snapshot was immutable — a reader could hold
one while the writer published another. Reuse trades that for storage that never grows between
scans, so a search must not overlap a refill. The work under the gate is a linear scan of the kind's
row array; the callers are the attribution pipeline's setup workers (one lookup per new flow plus a
retry), not the capture pump, so the serialization is below the noise floor of a scan that costs a
kernel enumeration.

### Reusable row storage

`OwnerTable` keeps two growable arrays and their counts. The fill side is
`internal Span<TcpOwnerRow> BeginTcpFill(int rowCount)` / `BeginUdpFill(int rowCount)` (resize on
growth, never shrink); the query side is the existing `Lookup(FlowKey)`. `IsAvailable` is set by the
fill: an unavailable platform leaves the slot unusable, and the cache must not publish it — the
current rule ("a 'no table' answer is never cached as though it were a read") is preserved *and*
becomes stronger: since the slot is the same object, a later unavailable fill cannot leave a stale
positive answer visible, because `IsUsable` is re-evaluated on the same object.

### Row types lose `IPAddress`

`TcpOwnerRow` already carries `Endpoint`s, but the reader builds them from `new IPAddress(bytes)`;
that conversion is the 738.8 MiB. `Endpoint.From(IPAddressValue, ushort)` exists and allocates
nothing, and the native rows are raw bytes:
`IPAddressValue.FromIPv4(ReadOnlySpan<byte>)` / `FromIPv6(ReadOnlySpan<byte>, scopeId)`.
`UdpOwnerRow` changes from `(IPAddress Address, ushort Port, uint ProcessId)` to
`(IPAddressValue Address, ushort Port, uint ProcessId)`; its predicate compares
`IPAddressValue`s directly instead of converting per row.

### Native buffer retention

`IPHelperOwnerTableReader` becomes an instance type holding one `nint` buffer that grows
(`Marshal.ReAllocHGlobal`) instead of allocating and freeing one per scan. The size probe
(`GetExtendedTcpTable(null, ref size, …)`) stays; the second call writes into the retained buffer.
A buffer that is short because the table grew between the two calls is handled by the existing
retry-free path: the second call returns `ERROR_INSUFFICIENT_BUFFER`, which today throws — keep the
throw and the row-count validation exactly as they are, so failure behaviour is unchanged.

## Invariants to hold

1. A scan allocates 0 B of managed memory once the slot's arrays have grown to the table's size.
2. `Lookup` never returns an owner from contents older than the reuse window, and never returns a
   negative answer from contents older than the request.
3. No reference to a slot's storage escapes the gate.
4. `Unavailable` is never published, never answers, and cannot be filled: the shared constant is
   marked non-fillable at construction, because filling it would make one caller's rows visible to
   every caller that asks for "no table".
5. `ReadCount` counts successful publishes only (unchanged accounting).
6. The seam stays injectable: a fake reader that returns a *fresh* table per `Read` remains valid,
   because the cache stores what it was handed and never compares instances.

## Observability

`ProcessOwnerTableCache` gains a read sink (`Action` invoked once per successful scan) and
`WindowsProcessAttributor` forwards it; `DurableCaptureBundle` composes
`RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionOwnerTableReads)` into it. The heartbeat
already reports counter deltas, so a campaign log gains an `attributionOwnerTableReads` series with
no heartbeat change.

## Verification

1. Unit: the existing `ProcessOwnerTableCacheTests` facts (unchanged assertions) plus a new
   allocation gate: script a multi-row table through the fake reader, warm up one scan, then assert
   `GC.GetAllocatedBytesForCurrentThread()` is exactly 0 across a window-answered hit, a
   forced-refresh hit and a negative lookup. The gate fails if `IPAddress` conversion or a per-scan
   array comes back.
2. On-host: 20 cps REL 120 s with `dotnet-counters`; compare mean `total_allocated` and the private
   memory series against the baseline captured in this task's `research/`.
3. Attribution: `dotnet-trace --profile gc-verbose` on the same load; the owner-table stack must be
   below 5 % of sampled allocation.
4. Campaign: one `wf-fdd-opt` full-plan row; analyzer gates green and REL/latency/throughput inside
   the pre-fix row's noise.

## Rollback

The change is confined to four production files and one test-support fake; `git revert` of the task
commit restores the immutable-snapshot reader. No config, no wire format, no CLI surface changes.

## Tradeoffs considered

- **Pooled arrays with a return-on-retire**: rejected — a reader can hold the previous snapshot while
  the next scan reuses its array, which is a torn read, and there is no safe retire point without
  hazard pointers or epochs.
- **Searching the native buffer directly and dropping `OwnerTable`**: rejected — it moves the
  predicate into the native reader, which makes the scripted seam fake the very logic under test.
- **Bounded per-scan allocation (compact rows, fresh array per scan)**: cheap and safe, but still
  O(rows) per scan and still ~0.6 MB/s at the measured scan rate; the project's convention is that a
  steady-state path carries an exact 0 B gate, not a budget.
