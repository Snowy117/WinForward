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

### Native buffer: per scan, spelled like the rest of the tree (retention decided against)

The reader stays an instance type with one slot per kind, but it does **not** retain a native buffer:
each scan allocates and frees one through `NativeMemory` (the release moved off
`Marshal.AllocHGlobal`/`FreeHGlobal`, the last such spelling in `src/`), so one unmanaged
allocation and one system-wide enumeration remain per scan. The same pass put the buffer back on
`void*`: the `nint` spelling came from `Marshal.AllocHGlobal`/`IntPtr` and the
`GetUnicastIpAddressTable(ushort, nint*)` companion, while this repository reserves `nint` for
handles (`adapterHandle`, `win32Event`, `nint.Zero`) and spells memory buffers as raw pointers.
`IPHelperTables.ReadRow<T>` is shared with the unicast reader, so the honest spelling moved there
too and `UnicastAddressInventory` (production and tests) followed — no cast remains on either
path. Retention was designed and rejected:
four kinds fill under four independent gates, so a shared buffer is a data race unless it is per
kind; freeing it needs an `IDisposable` chain the composition does not have (reader and attributor
are process-lifetime); and the retained size would sit at the widest table ever seen, which is the
committed-memory budget this task exists to shrink. Recorded as not implemented, not as pending.

## Invariants to hold

1. A scan allocates 0 B of managed memory once the slot's arrays have grown to the table's size.
2. `Lookup` never returns an owner from contents older than the reuse window, and never returns a
   negative answer from contents older than the request.
3. No reference to a slot's storage escapes the gate.
4. `Unavailable` is never published, never answers, and cannot be filled: the shared constant is
   marked non-fillable at construction, because filling it would make one caller's rows visible to
   every caller that asks for "no table".
5. `ScanCount` counts successful fills only (unchanged accounting).
6. The seam stays injectable: a fake reader that returns a *fresh* table per `Read` remains valid,
   because the cache stores what it was handed and never compares instances.

## Observability

`ProcessOwnerTableCache` gains a read sink (`Action` invoked once per successful fill, alongside
`ScanCount`) and `WindowsProcessAttributor` forwards it; `DurableCaptureBundle` composes
`RuntimeCounters.Shared.Increment(RuntimeCounters.AttributionOwnerTableScans)` into it. The landed
counter is `attributionOwnerTableScans` (the design drafted `attributionOwnerTableReads`): the
heartbeat already reports counter deltas, so a campaign log gains that series with no heartbeat
change.

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

## Recorded design trade-offs from the review

- **The slot/gate coupling is a documented timing invariant, not a type-level one** (S11). `OwnerTable`
  now carries a begin → write → complete protocol plus an availability state, and the rule that a
  search must run under the same per-kind gate as the refill is stated in the interface and type
  docs. It holds because the cache owns both the gate and the reader's slot; a second reader
  implementation or a second cache instance would have to re-establish it. Keeping the slot in the
  reader is what lets a fake replace the native enumeration in tests, which is the trade this task
  chose.
- **The non-fillable constant is a constructor boolean** (S12), where the repository's deep-module
  guideline prefers ownership expressed in the signature. Kept because it guards exactly one shared
  instance (`OwnerTable.Unavailable`) and a subtype would widen the seam for every implementer; the
  guard throws rather than silently mutating shared state.
- **`OwnerTableCache.ReadSink` is invoked while the per-kind gate is held** (S13), unlike
  `NativeBufferPool.AccountingSink`, which runs on the lock-free rent/return path. The sink contract
  ("composition sets it once, it must not throw, it never influences behaviour") is the same; moving
  the call outside the gate is a future unification, not a defect in this change.
- **Commit-title wording** (S7): "allocates nothing" means the managed heap. The PRD and the spec say
  so exactly; the title is not rewritten.

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
