# Design — F5 pump I/O shape: speculative batched read and event-driven idle wake

Task `.trellis/tasks/10-01-pump-io-shape` (finding F5 of `09-29-tcp-udp-path-structural-perf`).
Inputs: `prd.md` (requirement contract, not weakened here), `research/implementation-notes.md`
(code-grounded anchors D1–D13), the recorded baseline
`benchmarks/results/2026-09-29-benchmark-coverage/pump-idle-wake.jsonl`, and the three landed
predecessors (F3 `09-30-expiry-sweep-bounded-pause`, F2 `09-30-warm-path-lock-chain`,
F4 `09-30-flow-key-parse-once`) whose facts are the regression surface.

Design rules this document obeys, from the predecessor tasks: **counts and bytes are gates, timing is
a series**; every proof states whether it is exact or a reading, and whether it is seam-level only;
every mechanism is measured through the same instrument before and after; and nothing that only a
Windows driver can answer is assumed.

---

## 1. Chosen mechanism per site

| # | Site | Today | Chosen mechanism | Kind of proof |
|---|---|---|---|---|
| 1 | `NdisApiDriver.TryReadPackets` (`NdisApiDriver.cs:136-166`) | `GetAdapterPacketQueueSize` (IOCTL #1), then the batched `ReadPackets` (IOCTL #2) only when non-empty (`:156-161`) | **Speculative read first**; the query is retained *only* as the disambiguator of a **non-successful** read. Requested count becomes the full batch capacity (the vendor's documented usage). | exact call counts at a new driver↔native seam, plus a recorded red-before |
| 2 | `NdisCapturePump.PaceIdle` (`NdisCapture.cs:312-318`, called at `:295`) | `Thread.Sleep(_pollDelay)` — measured 877–886 polls/s and 1.78–1.85 % of a core per idle second | **Bounded wait on the driver's packet-arrival event** behind an injectable `INdisPacketArrivalSignal`; the sleep remains the fallback when no signal is installed | exact wait counts + a 0 B gate at the seam; the CPU/latency drop is a series |
| 3 | Read-call counting | none below `INdisPacketReader` (notes §4) | New `INdisReadPacketCalls` seam + production read counters on the driver | exact counts over a scripted fake; the counters are the Windows-experiment instrument |
| 4 | Event binding | `SetPacketEvent` not even declared (notes §2.2 / D4) | New pinned `[LibraryImport]` + `NdisApiDriver.TryRegisterPacketEvent` returning a disposable `NdisPacketArrivalSignal`, wired per adapter per generation in `NdisCaptureGenerationFactory` | fails open to today's poll shape; hardware behaviour is an open item (§9) |
| 5 | The unverified "full-capacity request fills short counts" ABI assumption | nothing (the assumption does not exist today) | **Self-healing guard** (§3.5): the first `read failed + query > 0` observation arms a sticky per-handle query-first shape, logs one `adapter.readShape.mismatch` diagnostic, and retries the drain once — so a mismatching ABI self-corrects instead of degrading a healthy adapter | exact call-shape facts + a recorded red that shows the degradation the guard removes |

Ordering: the instrument lands first and proves the *pre-change* shape red (Step 1), then the read
shape flips and its self-healing guard lands with it (Step 2 + 2b, one commit — an unguarded read-first
tree is the configuration the operator rejected), then the idle wait (Step 3), then the Windows binding
and composition wiring (Step 4), then evidence and spec (Step 5). Each step is independently revertible
except 2b, which reverts with Step 2 (§10).

---

## 2. Site 1 — the read shape

### 2.1 The shape

```
TryReadPackets(adapterHandle, buffers):
  if buffers.Length == 0 -> 0                                  (validation, unchanged)
  lease = adapterGate.Enter()
    readSucceeded, packetsSuccess, readError = calls.ReadPackets(adapterHandle, buffers, buffers.Length)
    if !readSucceeded:
        querySucceeded, queued, queryError = calls.GetAdapterPacketQueueSize(adapterHandle)
    release lease
  classify (§2.3)
```

Three changes, all inside the same gate lease and none of them on the pump side:

1. The read is issued **unconditionally** (no preceding query).
2. `dwPacketsNumber = buffers.Length` (32 for the pump) instead of `min(queued, capacity)`. This is the
   vendor's own usage: every shipped sample requests the full buffer capacity and reads the actual
   count from `dwPacketsSuccess` (`examples/…/TestDotNet/Program.cs:275-323`, `ndisapi.net/ndisapicl.cpp:139-172`).
   A request for 32 slots costs 16 + 8·31 = 264 bytes of stack (`MultiRequestByteCount`; `MaxStackMultiRequestBytes` = 1024,
   `NdisApiDriver.cs:25/173`) — the `NativeMemory` overflow path stays, now unreachable for this caller.
3. The query runs **only when the read did not succeed**, and only to answer one question: was there
   anything to read?

### 2.2 Why the query cannot simply be deleted

`ReadPackets` returns `BOOL`; the pinned user-mode wrapper is a passthrough of `DeviceIoControl`
(`ndisapi.cpp:872-965`), so "empty queue" and "driver error" are the same observable (`FALSE`) unless
the kernel driver distinguishes them. The strongest evidence available — the vendor's own drain loop,
which terminates on `FALSE` and would spin forever on `TRUE`-with-zero — points at `FALSE`, but it is
an inference from a sample, not a specification (notes §2.3). Deleting the query outright would make
the first idle poll of every adapter a possible `FALSE`-on-empty: with a transient-classified error
(31 / 170 / 995 are in the table, `IsTransientReadError`, `NdisNativeCallStatus.cs:48-54`) that is a
retry storm and then a **degraded adapter**, i.e. silent loss of interception, on the most common
state a network adapter is in.
`prd.md:24-26` anticipates exactly this: if the ABI cannot distinguish empty from error, the query
stays as the fallback and the outcome is recorded, not smoothed over.

### 2.3 The classifier (the empty-queue semantics, pinned)

`NdisNativeCallStatus.InterpretBatchReadResult` is re-ordered; the total function becomes:

| `ReadPackets` | query issued? | query result | Outcome |
|---|---|---|---|
| `TRUE` | no | — | `min(dwPacketsSuccess, buffers.Length)`. **`0` means the queue is empty** — no error, no extra call |
| `FALSE` | yes | `TRUE`, `queued == 0` | `0` — the queue was empty; the read's error is not an adapter fault |
| `FALSE` | yes | `TRUE`, `queued > 0`, **first occurrence for this handle** | the **ABI-mismatch guard** (§3.5): arm query-first, publish one diagnostic, retry the read once in the query-first shape and return its count |
| `FALSE` | yes | `TRUE`, `queued > 0`, guard already armed **or** the guarded probe failed | `Win32Exception(readError, "…read … from a non-empty queue (… queued {n}, requested {n} …)")` — unchanged message and native error, i.e. today's path |
| `FALSE` | yes | `FALSE` | `Win32Exception(queryError, "Unable to inspect the NDISAPI packet queue…")` — fail-closed, unchanged helper |

Helpers — **three**, one per classification outcome, and all three live in `NdisNativeCallStatus` so the
"single classification site" property survives (review MINOR 8):

1. `ClampReadCount(uint packetsSuccess, int requestedCount)` — the success branch, replacing
   `InterpretBatchReadResult`. Same defensive clamp (`Math.Min(packetsSuccess, requestedCount)`).
2. `HasQueuedPackets(int queryResult, int queryError, uint queuedPacketCount, nint adapterHandle)` —
   reused **verbatim** (`NdisNativeCallStatus.cs:17-21`) for the fallback branch's query-failure throw,
   and it keeps its existing fact unchanged.
3. `ThrowReadFailedOnNonEmptyQueue(int requestedCount, int readError, nint adapterHandle)` — the
   non-empty-queue throw, keeping the message text and native error asserted at
   `NdisApiAbiTests.cs:64-67` (rewritten to the new signature, not weakened). Its `requested` argument is
   now the **full capacity** rather than `min(queued, capacity)`, so the fact's expected message changes
   from `requested 4` to the capacity the test drives — the assertion gets stronger, not looser,
   because the message now reports what the driver was actually asked for.

Both rewritten facts stay in `NdisApiAbiTests` (the classifier's home), not in the new call-shape class:
that class proves *which calls happen*, this one proves *what each outcome means*.

### 2.4 Fail-closed equivalence with today (the load-bearing argument)

Today, "empty" is decided by the query in **all** cases; the read's `FALSE` ⇒ throw with a non-empty
queue. After the change, "empty" is still decided by the query whenever the read is not successful,
and by `dwPacketsSuccess == 0` when it is. The detection surface is unchanged:

| Driver state | Today's outcome | After | Same? |
|---|---|---|---|
| queue non-empty, read OK | query>0, read OK → n | read OK → n | yes |
| queue non-empty, **fewer packets than requested**, read OK with a short count | query=n, read `min(n, cap)` → n | read `cap` → n (short count) | yes **iff** the ABI fills short counts — the new, acceptance-relevant open item (§9.1) |
| queue empty, read OK-with-0 (hypothesis B) | query=0 → 0 (read never issued) | read OK-0 → 0 | yes, and now with **0 queries** |
| queue empty, read `FALSE` (hypothesis A) | query=0 → 0 (read never issued) | read `FALSE`, query=0 → 0 | yes — one query either way, only its position moves |
| queue non-empty, read fails — **first** occurrence for this handle | query>0 → throw(readError) | read fails, query>0 → **guard**: one extra read in the query-first shape; success ⇒ n, failure ⇒ throw(readError) | outcome identical on failure; on success the packet is delivered instead of the adapter degrading (§3.5) |
| queue non-empty, read fails — subsequent occurrences | query>0 → throw(readError) | query-first shape (sticky) → read fails → throw(readError) | **yes, byte-identical call shape and classification** |
| adapter broken, query fails | throw(queryError) | read fails, query fails → throw(queryError) | yes |
| adapter broken, query succeeds with 0 | 0 forever (pre-existing blind spot: the read is never attempted) | 0 forever, same blind spot | **unchanged — not newly introduced** |
| `buffers` holds a null hole, queue empty | 0 — the request is never built, so the null is never dereferenced | `ArgumentNullException` from `BuildMultiRequest` on the first drain | **changed, ABI layer only**: today's `0` half of `windows-ndisapi.md:427` was an artefact of the query guard (notes §2.5 item 2). The pump's buffers are never null (`NdisCapture.cs:143-144`) |

The last two rows matter for the reviewer. Today's fail-closed guarantee already rests on *the
queue-size query failing when the adapter breaks*, never on the read result — the reordering does not
widen that blind spot by one state, it only relocates which call is attempted first. And the request
now covers the whole caller array on every drain, which is what makes the null-entry contract reachable
where it previously was not. This is the argument Step 2 must re-prove by fact (§7 AC1), not merely
assert.

### 2.5 Costs, accepted and recorded

- **Under load:** 2 IOCTLs per drain → **1**. This is F5.1's win.
- **Idle under hypothesis B:** 1 read per poll, 0 queries (today: 1 query).
- **Idle under hypothesis A:** 1 read + 1 query per poll (today: 1 query). The poll *count* is what
  F5.2 removes (~886/s → ~10/s with a 100 ms timeout), so the idle IOCTL rate falls by ~44× even in
  the worst hypothesis: 886 → ~20/s.
- **An extra seam-level read per idle→active transition** (the empty read that parks the pump). In a
  burst the pump reads a batch per iteration and never waits, so the steady-state per-packet read
  count is unchanged; in a sparse pattern (one packet per idle period) the pump issues one empty read
  and one frame read per packet. The wake row's `readsPerWake`/`readCallsPerPacket` moves 1.0 → 2.0,
  but **that is not a like-for-like comparison**: the old proxy row counted only frame-delivering reads
  because the empty read was hidden inside its parked `TryReadPackets` call. The row's note must say
  "one extra *seam-level* read per wake relative to the proxy's accounting; one IOCTL replaces ~886
  empty-queue queries per second", and `readCallsPerPacket` is **not** an acceptance figure in this
  task.
- **A failed read now costs one extra query** before the retry backoff. Errors are rare and the path
  is fail-closed.
- **A short-count read must succeed** when the request exceeds the queue depth (§2.4 row 2). This is a
  new ABI assumption, not a cost: if it fails, the queue-first rollback applies (§9.1).
- **One small win worth recording**: the returned count may now exceed what the query reported at the
  same instant (the queue grew between the query and the read), because the request is no longer capped
  by a stale queue size — more packets per drain, fewer iterations, harmless.

### 2.6 Rejected alternatives (recorded so they are not re-explored)

| Alternative | Why not |
|---|---|
| Delete the query entirely (pure speculative read) | Unsafe under hypothesis A: error 31/170/995 on the most common driver state degrades every idle adapter. The PRD's own fallback clause forbids it |
| Keep query-first, record a negative result (PRD's literal fallback) | Loses the entire under-load win and leaves the task as instrumentation only. Retained as **Step 2's abort shape** if the seam facts show the disambiguating query cannot be made exact (§`implement.md` Step 2 rollback) |
| `ReadPacketsUnsorted` / `ReadPacket` (single, unsorted) | Different IOCTLs with different semantics (`IOCTL_NDISRD_READ_PACKETS_UNSORTED`), no per-adapter request handle in the same shape, and a second code path to keep fail-closed. Out of scope |
| `dwPacketsNumber = min(lastBatchCount, capacity)` (adaptive request size) | Same IOCTL count as the full-capacity request (the driver fills what it has), plus a state variable to get wrong. No win |
| Query only every K-th poll (amortised query) | Keeps the query exactly where the requirement says it must not be (before the read) and still pays it under load |
| Ask the driver for the queue size *after* a successful read to detect loss | Extra IOCTL on the hot path; nothing consumes the answer |

---

## 3. The read-call counting instrument

### 3.1 The seam

New file `src/WinForward.NdisApi/NdisReadPacketCalls.cs` (interface + its single implementation in
one file, per `directory-structure.md:65`):

```csharp
/// The driver's two read-path native calls behind one injectable seam. The seam exists so the
/// read shape (which call, in what order, with what requested count) is exactly provable without
/// ndisapi.dll; it is not a general native-call abstraction (the send/mode surfaces are untouched).
internal interface INdisReadPacketCalls
{
    bool GetAdapterPacketQueueSize(nint adapterHandle, out uint queuedPacketCount, out int nativeError);
    bool ReadPackets(nint adapterHandle, NdisPacketBuffer[] buffers, int count, out uint packetsSuccess, out int nativeError);
}
```

`NdisNativeReadPacketCalls` (same file, nested or sibling) holds the `NdisApiSafeHandle` and absorbs
the code currently at `NdisApiDriver.cs:170-198` (`ReadPacketsBatch`, `ReadPacketsRequest`) plus the
`GetAdapterPacketQueueSize` call. The two seam methods deliberately mirror the native entry-point names
— after the move `NdisApiDriver` no longer calls either static directly on the read path, so the
mirroring keeps the call graph readable; the `[LibraryImport]` declarations themselves stay in
`NdisApiNative` even where the driver stops calling them (`directory-structure.md:72`: the ABI
directory keeps the full declaration set).
**`BuildMultiRequest` and `MultiRequestByteCount` stay on `NdisApiDriver`** as `internal static`
(`MultiRequestByteCount` widens from `private`; the request builder's doc sentence about tests is
correct after all) and `MaxStackMultiRequestBytes` widens to `internal const`, because three facts call
the builder directly — `NdisApiBatchedSendAbiTests.cs:36/60/79` (notes D13, a planning error the
independent review caught: the original plan would not have compiled). The read-calls class calls the
same helper, so the null-slot guard of `windows-ndisapi.md:427` keeps one implementation and one
reachable fact.
`NdisApiDriver` keeps `MaxPacketsPerSendRequest` and gains:

```csharp
private readonly INdisReadPacketCalls _readCalls;           // production: NdisNativeReadPacketCalls
internal static NdisApiDriver CreateForTests(INdisReadPacketCalls readCalls);   // handle 0 ⇒ Dispose is a no-op
```

`CreateForTests` uses `NdisApiSafeHandle.FromRawHandle(0)`: `SafeHandleZeroOrMinusOneIsInvalid` treats
0 as invalid, so `Dispose()` never reaches `NdisApiNative.CloseFilterDriver` and no test can trip the
DLL resolver on Linux. Cost on the hot path: one interface dispatch per IOCTL (no managed allocation)
against a syscall that costs microseconds — priced, not free, and stated as such.

### 3.2 What the instrument proves, and what it does not

The fake records **(call, arguments, order)**. It therefore proves, exactly:

- the happy path issues **1 `ReadPackets` and 0 `GetAdapterPacketQueueSize`** per drain, with
  `count == buffers.Length`;
- the query can **never precede** a read (the recorded call order is `[ReadPackets]` or
  `[ReadPackets, GetAdapterPacketQueueSize]`);
- the empty-queue outcome under **both** ABI hypotheses (`TRUE`+0 and `FALSE`+query-0) is `0` packets,
  no exception, and at most one query;
- a failed read with a non-empty queue still throws the read's native error; a failed query still
  throws the query's.

It does **not** prove: which hypothesis the real driver implements; whether a full-capacity request
fills short counts (§9.1); that a real IOCTL round trip happens the recorded number of times; or
anything about kernel-side cost. Those are §9's items.

### 3.3 The production counters and their exact increment sites

`NdisReadDiagnostics(BatchReads, QueueSizeQueries, EmptyReads, FailedReads, LastFailedReadNativeError)`
is added next to `NdisPumpDiagnostics` and is what a Windows run reads to decide the ABI hypothesis, so
the increment sites are part of the instrument's contract — an off-by-one site makes §9.2's A-vs-B
discrimination unsound:

| Counter | Increment site (all inside `TryReadPackets`) |
|---|---|
| `BatchReads` | once per `ReadPackets` call, **whatever the result** |
| `QueueSizeQueries` | once per `GetAdapterPacketQueueSize` call |
| `FailedReads` | once per **non-success** `ReadPackets` return, **immediately after capturing `readError` and before any classification/throw** — this is the counter that distinguishes hypothesis A |
| `EmptyReads` | once per drain whose **classified** result is 0: read-success-with-0, *or* read-`FALSE` classified empty through a query returning 0 |
| `LastFailedReadNativeError` | on every non-success read (so hypothesis A's error code is readable even while the queue is empty) |
| `ReadShapeMismatchCount` | once per **arming** of the §3.5 guard (at most once per adapter handle per driver lifetime) |

So hypothesis B reads as flat `FailedReads`/`QueueSizeQueries` with growing `EmptyReads`, and
hypothesis A as `FailedReads` ≈ `QueueSizeQueries` with growing `EmptyReads` too; the pair is what
separates them. `Interlocked` increments per IOCTL, 0 B, no lock. A `NdisApiReadShapeTests` fact reads
the snapshot after each scripted drain, so the counters are exercised rather than trusted (F4's
unused-seam lesson).

### 3.4 The driver read path's byte gate

`TryReadPackets` runs once per pump iteration, so the new per-call work (one interface dispatch, the
counters, the classifier) gets the same exact treatment as every other window in the tree:
`NdisApiReadShapeTests.DriverReadPathAllocatesNoManagedBytes` drives `CreateForTests` with a
non-recording fake over 1,000 drains under the established window contract
(`hot-path.md:1047-1052`: bounded exactly-zero probe batches, `Assert.Equal(0, allocated)`, unchanged
managed thread id, a thread-independent call-count backstop, every assertion outside the window), and
`stackalloc` is native stack, not managed heap. Without it §5's "none" claims would be assertions
rather than measurements.

---

### 3.5 The self-healing ABI-mismatch guard

**The failure mode it removes.** §2.4's "queue non-empty, read fails" state is today unambiguously a
driver error and throws. The read-first shape adds a second way to reach it that has nothing to do with
a driver fault: an ABI that fails a batched request whose `dwPacketsNumber` exceeds the queue depth
(§9.1). The two are **indistinguishable on the first observation**, and today's classification turns
that state into a `Win32Exception` → transient retry (5 attempts, ~3.1 s) → **degraded adapter**, i.e.
silent loss of interception on hardware that is working perfectly. The operator rejected both
"detect it on Windows later" and a pre-emptive query, so the read shape heals itself.

**State machine** (per adapter handle, sticky):

| State | Entered by | Drain shape | Leaves |
|---|---|---|---|
| `read-first` (initial) | the adapter's first drain | one read; a query only if the read fails | on the first `read failed && query > 0` for this handle |
| `query-first` (sticky for the driver's life) | that observation, after the probe below | query → read only when non-empty (**today's exact shape**) | never |

Transition, inside the one adapter-gate lease:

```text
read(buffers.Length)
  success        -> return min(success, len)                          [no query: the conforming path]
  failure:
    query
      failure    -> throw queryError                                  [unchanged]
      queued == 0-> return 0                                          [empty, unchanged]
      queued > 0 -> *** guard point ***
                    if query-first already armed:
                        throw readError                            [today's path, unchanged]
                    else:
                        arm query-first (sticky) + publish one NdisReadShapeMismatch
                        read(min(queued, len))                      [the probe]
                          success -> return min(success, requested)
                          failure -> throw readError                [today's path, unchanged]
```

**Why the probe is one extra read, not a query+read pair**: the disambiguating query *is* the query of
the query-first attempt (it already reported `queued`), so the probe only issues the read that query
enables. The whole self-heal therefore costs exactly **one extra native read and one query per adapter
handle lifetime**, against a false degradation that costs the adapter until the next refresh.

**Sites.**

| Element | Site |
|---|---|
| the sticky flag | `NdisApiDriver`: `private readonly ConcurrentDictionary<nint, byte> _queryFirstAdapters`, probed with `TryGetValue` next to `_adapterGates.Get` (lock-free, no allocation, map only grows — the same lifetime model as the gate map); `internal bool IsQueryFirstForDiagnostics(nint adapterHandle)` for facts and the Windows run |
| counters | `NdisReadDiagnostics` gains `ReadShapeMismatchCount` — the seam tests' instrument and, like `NdisPumpDiagnostics`, `internal` (no production consumer today). A Windows run reads it through the stability/benchmark host (`WinForward.Benchmarks` is a friend assembly, `NdisApiAbi.cs:6-7`); the **product** run's read-out is the log line below — no `adapter.readShape.mismatch` means a conforming ABI, one line per adapter means it healed |
| the diagnostic | `public readonly record struct NdisReadShapeMismatch(nint AdapterHandle, int RequestedCount, uint QueuedPacketCount, int NativeError)`, published once per arming through `public Action<NdisReadShapeMismatch>? ReadShapeMismatchSink` (set with `Volatile.Write`) |
| the log | `NdisCaptureGenerationFactory.Create` sets that sink per generation, because it is the only place holding the driver, the generation's scope (handle → `StableId`/`FriendlyName`) and the logger: one warn `adapter.readShape.mismatch` with `adapter`, `requested`, `queued`, `nativeError`, rate-limited **per adapter handle** (`RuntimeLogThrottle`, one per handle, the `_slotExhaustedWarn` shape). The throttle is per handle and not per factory because the guard arms at most once per handle: a process-wide window would let the first adapter's arming suppress every sibling's single line, and "one line per adapter" is exactly what §9.1's experiment reads. The driver itself cannot log: `IRuntimeLogger` lives in `WinForward.Runtime`, and the dependency direction is `Runtime → NdisApi`. Precedent for a composition-wired sink on a shared component: `NdisPacketBufferPool.Shared.AccountingSink` (set once in `Program.cs`) |

**Why this is not the pre-emptive query requirement 1 removes.** The query is still absent from the
conforming path: a pump whose read-first drains succeed keeps zero queries and one IOCTL per drain, the
guard fires **at most once per adapter handle**, and it fires only on positive evidence (`read failed`
**and** the queue reported non-empty). On a conforming driver the only new per-drain cost is one
lock-free `TryGetValue` on a map that stays empty plus one branch; the requirement's "one read IOCTL
per drain under load" is untouched.

**Accepted costs, recorded rather than smoothed over.** A *false* positive (a genuine transient read
error while the queue is non-empty) arms the guard for that handle: one extra read on that occurrence,
then one extra query per non-empty drain for the rest of that handle's life. That is a performance
residual on one adapter until the next generation, never a correctness change (the throw path is
unchanged) — and it is the deliberate side of the trade the operator chose over losing the adapter's
interception for ~3.1 s. Two further residuals: the flag is keyed by enumeration handle on a durable
driver, so a refresh that reuses a handle value inherits it (`windows-ndisapi.md` already documents
pointer reuse; a per-generation reset would need a new driver surface, recorded as a follow-up); and
because the probe lives inside the driver, it is invisible to the pump's retry budget — which is exactly
what the guard fact asserts.

**Rejected alternative**: a second optional seam plus a pump option callback so the *pump* reports the
mismatch (mirroring `OnTransientRetry`). Rejected because it needs either a per-drain poll of the
driver (a new call on the packet path) or an extra loop ctor parameter for a cold one-shot event, while
the sink produces the same log line with one public property and no pump involvement.

---

## 4. Site 2 — the event-driven idle wait

### 4.1 The seam

```csharp
/// The adapter's packet-arrival signal (the ndisapi SetPacketEvent binding). Wait blocks the
/// caller until the signal is raised or the timeout elapses; the return value is advisory —
/// the pump re-reads on every wake and on every timeout alike, so a coalesced, spurious or
/// missed signal can only cost one timeout of latency, never a packet.
public interface INdisPacketArrivalSignal : IDisposable
{
    bool Wait(TimeSpan timeout);
}
```

Production implementation `NdisPacketArrivalSignal` (new file
`src/WinForward.NdisApi/NdisPacketArrivalSignal.cs`, which also **declares the interface**, per
`directory-structure.md:66` "put the interface beside its implementation, not in the consumer's file"
— the pump consumes it from the same assembly, so nothing else moves) wraps a `WaitHandle` and an
optional release callback; it is **not** `[SupportedOSPlatform("windows")]`, because a `WaitHandle`
wait is exactly what the pump needs on any OS and because that is what makes the idle path measurable
on this host:

```csharp
public bool Wait(TimeSpan timeout) => _arrival.WaitOne(timeout <= TimeSpan.Zero
    ? 0
    : (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue));
```

Pump wiring (`NdisCapture.cs`):

- `NdisCapturePumpOptions.PacketArrivalSignal` — public, nullable, **borrowed**: the pump never
  disposes it; the composition that created it owns it (the same convention as `OnBatchCompleted`).
- `NdisCapturePumpOptions.IdleWaitTimeout` — public, nullable; the default is
  `private static readonly TimeSpan s_defaultIdleWaitTimeout = TimeSpan.FromMilliseconds(100);`
  (`quality-guidelines.md:27`: `TimeSpan.FromMilliseconds` cannot be `const`, and private static fields
  are `s_camelCase`). The AC-2 fact reads it through an `internal TimeSpan IdleWaitTimeoutForTests`
  accessor or owns its own 100 ms literal — it must not depend on a private member.
- `PaceIdle()` becomes: `if (_arrivalSignal is null) Thread.Sleep(_pollDelay); else _arrivalSignal.Wait(_idleWaitTimeout);`
- Nothing else in `RunIteration` moves: the wait is still reached only after a read returned 0, after
  the batch-completed callback, after the cancellation check and after the `_stopped` check
  (`NdisCapture.cs:286-295`). The pump still reads *before* it ever waits, so start-up adds no latency
  and the drain-till-empty behaviour is the existing outer loop, untouched.

### 4.2 The timeout: why 100 ms

| Constraint | Effect |
|---|---|
| Idle CPU | today's 1.78 % is the *cadence* (877–886 timer wakeups/s × ~20 µs). A 100 ms bound ⇒ ~10 waits/s ⇒ ~0.02 % |
| Lost-wake latency bound | ≤ 100 ms, below Windows' initial RTO (~300 ms), so a single lost wake is a latency hit rather than a retransmission — the old shape caught the same packet within ~1.13 ms, which is the price of the 100 ms backstop |
| Generation-stop latency (production) | `TransactionalCaptureRuntime.StopAsync` (`CaptureLifecycle.cs:108-135`) cancels `_scope` (`:126`) **before** awaiting the run (`:129`); every parked pump therefore wakes from its own timeout **concurrently**, so the wall clock is ~one timeout for all adapters, and the sequential `await pump.DisposeAsync()` loop (`MultiAdapterCaptureLoop.DisposeCoreAsync:99-107`) then finds each run already finished. `RuntimeCaptureGeneration.DisposeAsync` → `_runtime.DisposeAsync()` → `StopAsync()` is the same ordering |
| Generation-stop latency (direct loop disposal) | a caller that disposes the loop without cancelling first pays **N × timeout**, because each `await pump.DisposeAsync()` sets that pump's `_stopped` and waits for its own remaining wait. No production path does this today; §9.7 controls it |
| Storm guard | the refresh storm guard is 1 s (`windows-ndisapi.md:173`); the added stop latency stays well inside it |
| Steady-state latency | the timeout is **not** the expected wake latency: with a live signal the wait returns on arrival (recorded proxy 0.078 ms p50), so the bound only applies to the lost-wake case |

The value is a **constant with an option override**, not a config surface: the Windows program can
tune it with data (§9), and the pump tests drive it explicitly.

### 4.3 Event-unavailable behaviour (fail-open to today's shape)

`TryRegisterPacketEvent` returns `false` (native `FALSE`, or `EntryPointNotFoundException` on an
older DLL) ⇒ the composition logs one rate-limited `capture.packetEvent.unavailable` warn with the
native error and passes **no** signal for that adapter. The pump then behaves exactly as it does
today: 1 ms `Thread.Sleep` polling, the same 0 B gate, the same disposal bound. Degraded performance
must never abort a capture run — the same posture as `HighResolutionTimerScope`'s fail-open
(`HighResolutionTimerScope.cs` doc) and it is what keeps the event an optimization rather than a new
correctness dependency.

### 4.4 Reset semantics: auto-reset, deliberately not the sample's manual-reset

The pinned vendor samples create a **manual-reset** event and call `Reset()` after the wait
(`network_adapter.h:146`, `TestDotNet/Program.cs:58/244/327`). Their loop has a lost-wakeup window: a
packet arriving after the drain loop exits but before `Reset()` is signalled into an already-set
event and then cleared, so it waits for the *next* packet. The estate's own analogue already solves
this: `NdisAdapterListWatcher` uses `EventWaitHandle(initialState: false, EventResetMode.AutoReset)`
with the documented rationale that "a driver signal raised while no waiter is parked stays set, so
nothing is lost between watcher-loop iterations" (`AdapterListWatcher.cs:19-27/52`).

With auto-reset: a signal raised between the pump's empty read and its wait leaves the token set, so
the wait returns immediately; a signal raised while no waiter exists is retained; multiple arrivals
coalesce into one token, which is sufficient because the wake re-reads and the loop keeps reading
while batches come back non-empty. The residual risk — the driver signalling *before* making the
packet readable, or signalling only once per empty→non-empty transition while the pump consumes the
token without draining — is bounded by the timeout and is §9's experiment 4. **Reviewers should attack
this deviation from the vendor sample**; it is a deliberate improvement, not an oversight.

### 4.5 Lifetime and ownership

| Object | Created | Released |
|---|---|---|
| `EventWaitHandle` + `NdisPacketArrivalSignal` | `NdisCaptureGenerationFactory.Create` (`NdisCaptureGeneration.cs:103-137`), once per in-scope binding, **before** the loop is constructed; if the loop construction throws, `Create` disposes what it already created | `MultiAdapterCaptureLoop.DisposeCoreAsync` (`:99-107`), **after** every pump has stopped and before the scope drain: registration released best-effort (`SetPacketEvent(handle, 0)`, failures swallowed like the mode restore), then the event disposed |
| The borrow | passed to the pump through `NdisCapturePumpOptions.PacketArrivalSignal` | never by the pump |

`MultiAdapterCaptureLoop`'s ctor gains a trailing optional
`IReadOnlyList<INdisPacketArrivalSignal?>? arrivalSignals = null`, **positionally paired** with
`bindings` (a mismatch is an `ArgumentException`), so ownership and pairing are visible at the call
site instead of hidden in a factory delegate; `DisposeCoreAsync` disposes the pumps first and then the
signals. Existing call sites (`NdisCaptureGeneration.cs:132-134`,
`NdisCaptureResilienceTests.cs:202/230`) keep compiling because the parameter is trailing and
optional. The driver outlives every generation (`Program.cs:282-288`), so the release always has a live
driver object.

Why per-generation and not per-driver: the binding is keyed on the enumeration handle, and every
refresh invalidates all handles at once (`windows-ndisapi.md:102-118`); re-registering on the new
handles is the same lifetime rule the mode controller already follows.

`SetPacketEvent` is reached through the **control gate** (cold, one registration per adapter per
generation, mirroring `SetAdapterListChangeEvent` at `NdisApiDriver.cs:100-109`) while a single
`[LibraryImport]` declaration is added to `NdisApiNative`:

```csharp
[LibraryImport(LibraryName, EntryPoint = "SetPacketEvent", SetLastError = true)]
[UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
internal static partial int SetPacketEvent(NdisApiSafeHandle handle, nint adapterHandle, nint win32Event);
```

Pinned from `wiresock/ndisapi@417b8734`: `include/ndisapi.h:307`, `ndisapi.cpp:3586`, `.def:16`, plus
the hardware export dump recorded at `.trellis/tasks/archive/2026-08/08-27-fix-datapath-throughput/implement.md:150-159`.

---

## 5. Memory and allocation shape

| Point | Allocation | Frequency |
|---|---|---|
| `INdisReadPacketCalls` dispatch | none | per IOCTL |
| `ETH_M_REQUEST` | `stackalloc` 264 B (native stack, not managed) | per read |
| Read counters | none (`Interlocked` on `long` fields) | per IOCTL |
| Guard probe (`_queryFirstAdapters.TryGetValue`) | none | per drain (one branch; the map stays empty on conforming hardware) |
| Guard publication (sink delegate + the readonly struct) | none | once per adapter handle lifetime (the closure itself is allocated at generation build — cold) |
| Seam dispatch + classifier | none | per drain |
| `EventWaitHandle` + `NdisPacketArrivalSignal` | one each | once per adapter per generation (cold) |
| `WaitHandle.WaitOne(int)` | none | per idle iteration |
| `IdleWaitTimeout` / `PollDelay` | value types | — |
| Pump loop body | **0 B managed** | per iteration |

Gates: the two existing idle 0 B gates stay green untouched
(`NdisCapturePumpTests.IdlePollIterationsAllocateNoManagedBytes:294-335`,
`CapturePumpReadCallTests.CountingReaderIdleIterationsAllocateNoManagedBytes:82-117`) because the
default (no signal) path is byte-identical to today; a **new** pump gate drives the production wait
path — a real `NdisPacketArrivalSignal` over an unsignaled `EventWaitHandle` with
`IdleWaitTimeout = TimeSpan.Zero` — so the wait entry point itself is inside an exact window on this
host, not only behind a fake; and a **new** driver gate (§3.4) covers the per-drain read path. The
zero-timeout gate exercises `WaitOne(0)`'s immediate-return path only; the *real* 100 ms timeouts are
covered by the `pump.idleEvent` scenario row, whose own `allocatedBytes == 0` throw is the gate (the
same shape as `pump.idle`, `PumpIdleWakeScenario.cs:92-95`) over ~150 genuine 100 ms waits in its 15 s
window. `hot-path.md:1034-1041`'s list of exact windows gains both new classes/facts.

---

## 6. Semantics and risk table

| # | Risk | Mitigation | Residual (recorded) |
|---|---|---|---|
| 1 | Real driver returns `FALSE` on an empty queue, and the returned error is in `IsTransientReadError` | The query disambiguates before any classification; the pump never sees an error from an empty queue | 1 extra query per idle poll; whether `FALSE`-on-empty is real is §9.2 |
| 2 | Real driver returns `TRUE` with `dwPacketsSuccess == 0`, and the estate's inference was wrong | The success branch returns 0 (empty) — the exact shape the PRD asks for | none; the query-on-failure path becomes a rarely-taken safety net |
| 2b | A full-capacity request **fails** when the queue holds fewer packets than requested (the new ABI assumption of §2.4) | None at this level — the mitigation is detection: the requirement-7 experiment (§9.1) and the query-first rollback | if it fails on hardware, a working adapter degrades after 5 retries with packets available; the rollback is mandatory, not optional |
| 3 | A signal is lost between the pump's empty read and its wait | auto-reset retains a signal raised while no waiter is parked; the timeout re-reads regardless | ≤ 100 ms single-packet delay; §9.4 |
| 4 | The driver signals level-ish and the event stays set → busy loop | auto-reset makes each wait consume one token; a spurious token costs one extra empty read | a real spin would show as an anomalous `EmptyReads`/idle-CPU reading in §9.5's experiment |
| 5 | Disposal latency grows from ~1 ms to the timeout | `_stopped` is still checked before the wait; the bound is restated for both disposal paths in §4.2 (production ≈ one concurrent timeout; direct loop disposal N × timeout) | generation stop adds ≈ one timeout for all adapters on the production path; §9.7 controls it |
| 6 | The binding leaks (event closed while registered) | registration released on dispose with a live driver handle; release is best-effort like the mode restore | a stale-handle release failure is logged once by the wiring, not fatal |
| 7 | The seam's interface dispatch or the counters add hot-path cost | one dispatch + one `Interlocked` per IOCTL, no allocation, priced against the syscall | gated, not assumed: §3.4's driver byte gate |
| 8 | An extra empty read per idle→active transition is read as a read-count regression | reported explicitly (`emptyReadsPerWake`), attributed, and not an acceptance figure (§2.5) | none, but reviewers should check the attribution |
| 9 | The classifier reorder weakens the transient-retry classification | §2.4's state table: the read's error is still thrown whenever the queue is non-empty; the retry table is untouched | the pre-existing "query answers 0 forever" blind spot is unchanged, not widened |
| 10 | Adding public knobs to `NdisCapturePumpOptions` breaks its documented shape | doc updated in the same commit (notes D8); the two `internal init` test seams keep their exact roles | none |
| 11 | The §3.5 guard fires on a genuine transient read error (false positive) | Bounded by construction: one extra read at the occurrence, then query-first for that handle only; the throw/retry/degrade path is unchanged, so no correctness delta | one extra query per non-empty drain on that adapter until the next generation; the handle-reuse inheritance is recorded in §3.5 |

---

## 7. Acceptance mapping — exact counts vs reported series

Every row names the artifact that discharges it. "Exact" means an assertion over a recorded call
sequence or a byte delta; "series" means a recorded reading that is compared, never gated.

| PRD criterion | Proof | Kind |
|---|---|---|
| **AC1 read shape**: one batched read per drain where the pre-change shape issued query+read; empty ⇒ zero packets, no error; the residual query only on the non-success path; red-before recorded | `NdisApiReadShapeTests`: `SpeculativeReadIssuesOneReadAndNoQueryForANonEmptyDrain` (order `[Read]`, `count == buffers.Length`), `SpeculativeReadNeverQueriesBeforeReading` (order assertion), `AnEmptyQueueYieldsZeroPacketsWithNoErrorUnderEitherReadResult` (both ABI hypotheses), `AFailedReadWithAFailedQueryStillThrowsTheQueryError`, `TheReadDiagnosticsCountOneReadPerDrainAndOneQueryOnlyOnAFailedRead`; **the §3.5 guard facts** (fires exactly once on the mismatching fake and the drain still succeeds; never fires on the conforming fake over a long series; the post-arming shape is exactly `[Query, Read]`; the mismatch never reaches the transient-retry budget; a failed probe still throws `readError`); **red-before** captured on the instrumented pre-change body (order `[Query, Read]`, `queries=1`) and, for the guard, on the read-first body *without* it (the mismatching fake degrades the pump: recorded `TransientReadRetryCount = 5` and `IsDegraded = true`) | exact |
| **AC2 idle wake**: one bounded event wait per idle iteration, not a sleep; `pumpIdleWake` re-run records idle CPU + wake latency against the baseline | `NdisCapturePumpTests`: `IdleIterationsIssueExactlyOneBoundedWaitAndNoSleepPacing` (waits == iterations, every observed timeout == `IdleWaitTimeout`, elapsed ≪ iterations × `PollDelay` with a 5000× margin), `WithoutAnArrivalSignalTheIdlePathKeepsThePollDelaySleep`, `AnArrivalSignalWakeEndsTheIdleWaitBeforeTheTimeout` (real `EventWaitHandle`), `DisposeWhileParkedInTheIdleWaitCompletesWithinTheTimeout`; series in `benchmarks/results/2026-10-01-pump-io-shape/README.md`: `pump.idle` (unchanged, ~886 polls/s, ~1.78 %) vs `pump.idleEvent` (~10 waits/s, CPU), and `pump.idleWake` (0.078 ms proxy) vs `pump.idleWakeEvent` | exact + series |
| **AC3 allocation**: idle poll gate and drain paths allocate 0 B | the two existing gates unchanged; new `IdleWaitIterationsAllocateNoManagedBytes` over the production `NdisPacketArrivalSignal` (real `WaitOne(0)` path) plus the `pump.idleEvent` row's own 0 B gate over ~150 real 100 ms timeouts; new `DriverReadPathAllocatesNoManagedBytes` over the per-drain driver body (§3.4); the drain path's packet handling is unchanged code | exact |
| **AC4 Windows items recorded** | §9's table, copied into the artifact README with experiment / expected observation / fallback — **including §9.1's partial-batch row, which the amended PRD makes acceptance-relevant and whose fallback is the query-first rollback**; the driver counters are the instrument | artifact |
| **AC5** Release zero-warning, full suite green, `dotnet format` empty, `jb inspectcode` zero | `implement.md`'s validation block at every rollback point | exact |
| **AC6** benchmark data recorded and cited before archive | `benchmarks/results/2026-10-01-pump-io-shape/{pump-idle-wake-before.jsonl,pump-idle-wake-after.jsonl,read-shape-counts.txt,class-totals.txt,gate-stability.txt,README.md}` | artifact |
| Requirement 4 (burst behaviour, batch size, fail-closed, single-pump degradation unchanged) | `CapturePumpReadCallTests.PumpMakesExactlyOneReadCallPerPollAndPerBatch` unchanged; `NdisCaptureResilienceTests` unchanged; `NdisApiAbiTests`' clamp/throw matrix rewritten but not weakened | exact |
| Requirement 5/6 (0 B, existing gates) | the full suite plus the four allocation classes, the F2/F3/F4 gate classes (`WarmPathGateTests` family, `SweepAllocationGateTests`, `FlowKeyShapeTests`/`PackedFlowKeyTests`/`PacketPathWalkCountTests`) stay green; `hot-path.md:1303`'s totals string refreshed | exact |

**Attribution discipline** (both F2 and F4 had a metric dominated by something else, so each proof is
attributed before it is relied on):

- The idle-CPU drop is attributed to **cadence**, not to pump work: the row reports `waitsPerSecond`,
  `emptyReadsPerSecond` and `pollsPerSecond` beside the CPU number, so a CPU change that is not
  accompanied by a cadence change is visible as unattributed.
- The read-shape counts are attributed to the **recorded call sequence of the seam**, never to
  "IOCTLs saved": the only claim is about which calls the driver makes, in what order.
- The wake-latency series is attributed to the pump's wait, not to the driver event (the event on this
  host is an OS event). §9.4 (signal loss) and §9.6 (wake cost) are the only places the real cost can
  be established.
- The read-shape *rate* claim is attributed to the idle-wait cadence, not to the read shape: the
  ~44–88× idle-IOCTL-rate drop of amended requirement 2 follows from ~10 waits/s replacing ~886
  polls/s, and the read shape contributes at most a factor of 2 within a poll. Both factors are
  reported separately (`waitsPerSecond` in the `pump.idleEvent` row; per-drain call counts in
  `read-shape-counts.txt`) so no single number carries two causes.
- Nothing in this task is credited with a throughput change; `CapturePumpBenchmarks` is run only as a
  no-regression companion.

---

## 8. Measurement plan

Commands (from the repo root; `--quick` caps the idle window at 15 s):

```bash
mkdir -p benchmarks/results/2026-10-01-pump-io-shape

# before-series (pre-change tree + the Step-1 instrument), 3 runs, one file then concatenated
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario pump --quick --output /tmp/wf-f5-before-$r.jsonl
done
cat /tmp/wf-f5-before-{1,2,3}.jsonl > benchmarks/results/2026-10-01-pump-io-shape/pump-idle-wake-before.jsonl

# after-series (landed tree), same three runs, one run per file then concatenated
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --stability --scenario pump --quick --output /tmp/wf-f5-after-$r.jsonl
done
cat /tmp/wf-f5-after-{1,2,3}.jsonl > benchmarks/results/2026-10-01-pump-io-shape/pump-idle-wake-after.jsonl

# no-regression companion (BDN short job, 3 runs, raw exports kept). The runner writes its BDN
# artifacts under --artifacts; a run without --output leaves nothing citable on disk for the
# stability rows, so every series command in this plan carries an explicit path.
for r in 1 2 3; do
  dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
    --filter '*CapturePump*' --job short --artifacts /tmp/wf-f5-pump-bdn-$r
done
# then copy each run's *-report.csv + *-report.md into benchmarks/results/2026-10-01-pump-io-shape/

# exact gates
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisApiReadShapeTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~CapturePumpReadCallTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisCapturePumpTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisApiAbiTests"
```

Scenario rows after this task (`PumpIdleWakeScenario.cs`, with the probe types split into
`Stability/PumpIdleWakeProbes.cs` — the file is 319 effective lines against the 400 limit):

| Row | Change | Reports | Kind |
|---|---|---|---|
| `pump.idle` | **unchanged** (no signal, 1 ms poll) | polls/s, CPU/idle-second, 0 B gate, readCallsPerPoll | the before/comparator series, and the proof that the fallback is byte-identical |
| `pump.idleEvent` | new: production `NdisPacketArrivalSignal` over an OS event, `IdleWaitTimeout = 100 ms` | `waits`, `waitsPerSecond`, `emptyReadsPerSecond`, CPU/idle-second, `allocatedBytes` (**gated 0**, thrown on a non-zero delta exactly like `pump.idle` at `:92-95`), `readCallsPerPoll` | exact (waits/reads/bytes) + series (CPU) |
| `pump.idleWake` | **unchanged** (reader-side blocking proxy) | p50/p95/p99/max, readsPerWake | recorded comparator |
| `pump.idleWakeEvent` | new: the pump parks in the signal wait; the harness arms the frame and sets the event | p50/p95/p99/max arrival→dispatch, `frameReadCallsPerWake` (1.0, exact), `emptyReadsPerWake`, the park-confirmation count | exact counts + latency series |
| `pump.idleWake.verdict` | extended | before/after pairs side by side | series |

**The `pump.idleWakeEvent` park-confirmation contract** (without it the row's samples can be hot
handoffs, which the existing row's own doc calls out as measuring nothing — `PumpIdleWakeScenario.cs:39-45`):
the measured signal is a thin scenario-local decorator over the production `NdisPacketArrivalSignal`
that **records entry into `Wait` and delegates to it**, so the production wait is still what blocks.
Per measured wake the harness:

1. waits until the decorator has observed exactly one more `Wait` entry than the previous wake
   (bounded timeout; a miss fails the row rather than reporting a latency);
2. sleeps the existing 1 ms park delay, so a wake that is *not* a blocked→ready transition needs the
   pump thread to be preempted between the entry count and the `WaitOne` call — a residual window the
   1 ms delay makes negligible, not impossible (the README's residual 8; the row still reports
   `signalDrivenReturns`, which rejects the timeout-driven sample but cannot separate a retained
   token from a real wake);
3. stamps arrival, arms the frame and sets the event;
4. asserts the pump dispatched exactly one frame.

The decorator also records the `timeout` argument it received and whether `Wait` returned before the
timeout, so a run whose arms stopped preceding the waits is visible in the row's own counters instead
of silently turning into a hot-pass series. Auto-reset makes step 1 load-bearing: an arm+set issued
before the pump parks would be *retained* and return immediately, so the entry-count assertion is what
separates a real wake from a hot handoff.

Numbers the after-series must move, quoted from the baseline in §`research/implementation-notes.md` §7:
`pollsPerSecond` 877.6/885.6/885.8 → ~10 waits/s; `cpuSecondsPerIdleSecond` 0.017798/0.018501/0.017826
→ ~2 × 10⁻⁴ (report-only; the row's own 0 B gate is the exact half); wake p50 0.0779/0.0783/0.0791 ms
→ the same order, now measured **at the pump** instead of through a reader-side proxy, and compared
against the ~1.13 ms poll cadence it replaces. `pump.idle`'s numbers must **not** move.

---

## 9. Windows open items

Every row is copied verbatim into the artifact README. "Fallback" is the concrete change if the
observation contradicts the expectation — never a weakened guarantee.

| # | Question this host cannot answer | Experiment (on Windows, real NIC + real driver) | Expected observation | Fallback if it fails |
|---|---|---|---|---|
| **1** | **Does a read requesting the full batch capacity succeed with a short count** when the queue holds fewer packets than requested? (PRD requirement 7; **acceptance-relevant**) | (a) leave the adapter idle ≥ 60 s **first** — the §3.5 guard cannot arm on an empty queue, so this half still reads the clean A/B counters of row 2; (b) then send 1..31 packets in bursts while the pump requests 32, and watch for the `adapter.readShape.mismatch` warn (the internal counter is readable in tests/benchmarks) | either **no** mismatch line and every short request returns its short count (the ABI conforms, 1 IOCTL per drain), or exactly one line per adapter carrying `requested=32`, `queued=n`, `nativeError=…`, the pump keeps running with **no** `adapter.retry`/`adapter.degraded`, and the drain shape is `[Query, Read]` from then on | **the guard handles it — that is its purpose; the experiment only confirms which shape the hardware takes.** If the guard itself ever fails (a second mismatch warn for the same handle, a degradation, or a drain shape that is neither), the query-first rollback of `implement.md` Step 2 applies and the reverted counts are recorded — never a tuning knob |
| 2 | Does `ReadPackets` return `TRUE`+0 or `FALSE` on an empty queue? | leave the adapter idle ≥ 60 s with the pump in capture mode, then read `NdisReadDiagnostics` — through the stability/benchmark host (`WinForward.Benchmarks` is a friend assembly, `NdisApiAbi.cs:6-7`) or a temporary log line, because the counters are `internal`; the product run's evidence is the log | exactly one of: `EmptyReads` grows with flat `FailedReads`/`QueueSizeQueries` (hypothesis B, 1 read/poll, 0 queries) or `FailedReads` ≈ `QueueSizeQueries` both grow (hypothesis A) and **no** `adapter.retry` / `adapter.degraded` event | hypothesis A is already safe; if `FailedReads` grows *without* a matching query outcome of 0 (i.e. the query reports a stale non-zero), revert to the query-first shape (`implement.md` Step 2 rollback) and record the reverted counts |
| 3 | Is the empty read's last-error inside `IsTransientReadError`? | same run, `LastFailedReadNativeError` | a single stable code; if it is 31/170/995, hypothesis A | confirms the disambiguating query is load-bearing; if the code is *not* in the table, the pure speculative read becomes viable and is recorded as a follow-up (not adopted without the fact) |
| 4 | Does the event fire for every arrival, or can a signal be lost between an empty read and the wait? | register the event, idle ≥ 60 s, send exactly one packet, measure arrival→dispatch; repeat N = 200 | N/N packets dispatched within a few ms; no packet waits the full 100 ms | lower `IdleWaitTimeout` (e.g. 25 ms) and re-measure; if loss persists, drop the event and keep the sleep fallback (the feature is an optimization) |
| 5 | Is the event level-ish (a spin) or one token per signal? | same run's idle CPU + `emptyReadsPerSecond` with no traffic | ~1/timeout empty reads per second, CPU ≈ 0 | revert the event (auto-reset already protects; a spin here would mean the driver holds the event set regardless of the object's reset mode) |
| 6 | What does the wake cost on hardware (`KeSetEvent` → dispatch), and what is arrival→dispatch on a real NIC? | same experiment as row 4, reporting the p50/p95/p99 | one order of magnitude below the ~1.13 ms poll cadence | none — this is a reported series; a bad number argues for a shorter timeout, not for a revert |
| 7 | Generation-stop latency with a parked pump, on both disposal orderings | (a) production: trigger a refresh (NIC disable/enable) while the link is idle, time `adapter.refresh` → pumps-stopped; (b) direct: dispose a `MultiAdapterCaptureLoop` with N parked pumps and no cancel, time it | (a) ≈ one timeout total (the cancel at `CaptureLifecycle.cs:126` precedes the awaits, so the pumps wake concurrently); (b) N × timeout, with no production caller | if (a) exceeds the refresh budget, adopt the deferred stop-handle variant (`research/implementation-notes.md` §5): a pump-owned `EventWaitHandle` + `WaitHandle.WaitAny([arrival, stop], timeout)`, which returns immediately on dispose. (b) is controlled, not fixed |
| 8 | Does the real DLL expose `SetPacketEvent` with the pinned 3-arg signature? | `dumpbin /exports ndisapi.dll` (already recorded 2026-08-27: present) + one live registration | registration returns `TRUE`; **no** `capture.packetEvent.unavailable` warn for that adapter (a success emits no line of its own), and that adapter's idle pump shows the arrival cadence — ~1 timeout of waits per second instead of ~886 polls/s, and a wake that returns on the event rather than on the poll | if registration fails, the design *is* the fallback: `capture.packetEvent.unavailable` warn + today's poll shape |
| 9 | `CreateEvent`/`KeSetEvent` on an **auto-reset** event (the vendor samples use manual-reset) | experiment 4 with `EventResetMode.AutoReset` | works; the pump does not spin (experiment 5) | switch to the sample's manual-reset + reset-after-wait, accepting its documented lost-wakeup window; re-run experiment 4 to bound it |

Out of scope and explicitly **not** claimed here: the Windows-only measurement program's rows
(they are the follow-up named by `prd.md:66-68`), any change to the pinned ndisapi ABI, and the
batched-send telemetry.

---

## 10. Rollback shape

| Step | Mechanism | Rollback |
|---|---|---|
| 1 | `INdisReadPacketCalls` + counters + rewritten ABI facts + the new read-shape facts + the before-artifact | revert the seam file, the driver's ctor/field, the helper visibility widening and the tests; delete the before-artifact. Behaviour-zero by construction (the body still queries first) |
| 2 | the read shape flip (the body of `TryReadPackets` + the classifier) | one-commit revert: restore the query-first body and `InterpretBatchReadResult`'s old signature. The seam and counters stay (they are the instrument) |
| 2b | the §3.5 self-healing guard (sticky flag + probe + diagnostic sink + factory wiring) | revert the flag, the guard point, the sink and its wiring. **This rollback re-opens the false-degradation risk**, so it is not a supported configuration on unverified hardware: reverting the guard requires reverting Step 2 with it (the two are one commit) |
| 3 | the arrival-signal seam + `PaceIdle` + `IdleWaitTimeout` + the pump facts | revert the option, the field and `PaceIdle`; the pump returns to `Thread.Sleep` and the existing gates are untouched |
| 4 | `SetPacketEvent` declaration + `TryRegisterPacketEvent` + the per-generation binding + the loop wiring | revert the wiring: `arrivalSignals: null`, no signal reaches the pump, and the behaviour is Step 3's default (sleep fallback) |
| 5 | evidence + spec rows | documentation only |

**Commit granularity (review MINOR 11).** Steps 1 and 2 land as **one commit**: a commit whose own new
facts fail is not shippable, and the whole point of the red-before is that the target-shape facts are
red on the instrumented pre-change body. The red is captured *between* the two half-steps of the same
work session — write the facts against the still-query-first body, run them, save the exact failure
text into `read-shape-counts.txt`, then flip the body and go green before committing. Revertibility is
unaffected because the two halves are disjoint hunks: reverting only `TryReadPackets`' body plus the
classifier restores query-first while the seam, counters and facts stay green (that *is* Step 2's
rollback, and it is also the failure fallback for §9.1/§9.2), and reverting the seam as well is
Step 1's rollback. Steps 3–5 are separate commits as listed.

No step leaves a half-applied mechanism: the merged Steps 1–2 without Step 3 is a strictly better read
shape, and Step 3 without Step 4 is the pump-side wait with no production binding (unreachable by
default, exercised only by tests and the scenario). Each intermediate commit compiles, keeps the full
suite green, and carries its own facts.

---

## 11. Spec rows F5 changes (spec-row → proof map)

| Spec row | After F5 | Proof |
|---|---|---|
| `windows-ndisapi.md:403` (`TryReadPackets` signature doc) | read-first; the query is the non-success disambiguator, not the steady-state prefix | `NdisApiReadShapeTests` order/count facts |
| `windows-ndisapi.md:414` ("the wait is bounded by the loop's poll delay because the loop observes the stop flag every iteration") | falsified by the arrival wait: the bound becomes the poll delay **or** the arrival timeout, and the two disposal orderings of §4.2 are named | `DisposeWhileParkedInTheIdleWaitCompletesWithinTheTimeout` (pump) + §9.7 (hardware) |
| `windows-ndisapi.md:415` (gate granularity "one lease per batch on the read path (query + read inside the same lease)") | one lease per read; the query joins it only on the failure branch | `SpeculativeReadNeverQueriesBeforeReading` |
| `windows-ndisapi.md:421` (query-failure row "Queue query native call fails \| throw") | now reachable only on the non-success path | `AFailedReadWithAFailedQueryStillThrowsTheQueryError` |
| `windows-ndisapi.md:424-426` (read error matrix) | new total table (§2.3): the empty row splits into `TRUE`+0 and `FALSE`+query-0, the clamp row's `requested` is now the full capacity, and **`:425` ("`ReadPackets` returns FALSE with a non-empty queue → throw") splits into first-occurrence (self-heal) and subsequent (throw)** | the two empty facts + the two throw facts + the clamp fact + the guard facts |
| `windows-ndisapi.md` (new row) read-shape self-heal | the guard: per-handle sticky query-first after one `read failed + queue non-empty` observation, one `adapter.readShape.mismatch` warn carrying adapter/requested/queued/nativeError, probe cost one extra read, retry budget untouched | the guard facts + §3.5 |
| `windows-ndisapi.md:427` (`buffers` empty or contains null entries \| 0 / `ArgumentNullException`) | the `0` half is gone for null **entries** (a full-length request always walks the whole array); empty still returns 0 | `BuildMultiRequestRejectsNullEntriesInsideTheRange` (kept) + a new `NdisApiReadShapeTests` fact that a null hole throws on a drain whose read would otherwise report empty |
| `windows-ndisapi.md:437-440` (Tests Required) | add `NdisApiReadShapeTests` (call-shape, classifier, counters, byte gate) and the new pump idle-wait facts | Step 5's spec edit, citing the facts |
| `windows-ndisapi.md:98` (perf note: 1 ms poll + "optional next upgrade") | the event wait is the primary idle path; the poll delay is the fallback | `IdleIterationsIssueExactlyOneBoundedWaitAndNoSleepPacing`, `WithoutAnArrivalSignalTheIdlePathKeepsThePollDelaySleep`, the byte-identical `pump.idle` row |
| `windows-ndisapi.md:278` (stale `BatchedSendFlushCount`/`BatchedSendPacketCount` row, notes D1) | correct the row to name the counters that exist, or record that the driver exposes none | `rg` evidence recorded in the artifact README |
| `windows-ndisapi.md` (new row) `SetPacketEvent` | 3-arg pinned import; per-adapter, per-generation registration; auto-reset app-owned event; NULL release; fail-open to sleep polling | the pinned upstream anchors + the registration/failure matrix row (a success emits no line; a refusal is the `capture.packetEvent.unavailable` warn) + §9.8 |
| `async-lifetime.md:131-133` (`NdisCapturePump` per-owner note: "owns no CTS at all") | still true — the pump gains **no** scope handle and disposes no borrowed signal; the new `IDisposable` lives on the loop | the loop wiring fact (signals disposed exactly once, after the pumps) |
| `async-lifetime.md:358-368` (C4 per-owner table) | `MultiAdapterCaptureLoop`'s row gains the per-generation `INdisPacketArrivalSignal` list it owns and releases in `DisposeCoreAsync` | the same fact |
| `hot-path.md:1034-1041` (the exact-window class list) | add the new idle-wait 0 B gate **and** the driver read-path gate | `IdleWaitIterationsAllocateNoManagedBytes`, `DriverReadPathAllocatesNoManagedBytes` |
| `hot-path.md:1295-1303` (per-gate proof totals string) | refresh `CapturePumpReadCallTests:n NdisCapturePumpTests:m`; `NdisApiReadShapeTests` is a structural class with its own repetition proof | Step 5's `class-totals.txt` |
| `NdisCapture.cs:34-36` (options doc: "the two internal members") | count and role restated after the two new public knobs | code + doc in the same commit |
| `NdisCapture.cs:59-94` (pump class doc: waits bounded by the poll delay) | waits bounded by the poll delay **or** the arrival timeout | `DisposeWhileParkedInTheIdleWaitCompletesWithinTheTimeout` |

---

## 12. Dead ends recorded so they are not re-explored

- **Pure speculative read (query deleted).** Unsafe under hypothesis A: `FALSE`-on-empty with a
  transient-looking error degrades idle adapters. Rejected on fail-closed grounds, not on cost.
- **Manual-reset event + reset-after-wait (the vendor sample shape).** Reintroduces the sample's
  lost-wakeup window; auto-reset retains a token for a signal raised with no waiter parked
  (`AdapterListWatcher.cs:19-27`). Recorded with the deviation rationale in §4.4.
- **Unbounded `WaitForSingleObject(event, INFINITE)`** (research F5.2's literal sketch). Makes
  disposal unbounded and removes the lost-wakeup backstop; the PRD pins the disposal/cancellation
  contract to the timeout.
- **A pump-owned stop handle + `WaitAny` for instant disposal.** Deferred, not rejected: the PRD's
  requirement 3 fixes the timeout as the bound, and the extra handle widens the seam for a ≈100 ms
  win that the 1 s storm guard absorbs. Recorded as §9.7's fallback if hardware disagrees.
- **Moving `BuildMultiRequest`/`MultiRequestByteCount` into the new seam file.** Planned and withdrawn
  before implementation: three existing facts call the builder on `NdisApiDriver`
  (`NdisApiBatchedSendAbiTests.cs:36/60/79`), so the read-calls class calls the driver's helper
  instead (notes D13). The alternative — repointing those facts — was rejected as a larger diff than
  the move it enables, with no gain.
- **A per-window control that could tell a host allocation lump from a driven allocation.** Closed by
  `09-30-exact-gate-residual-lumps`; the gates keep their exact zero and their per-gate proof loop.
- **Relaxing any 0 B gate to admit "small" per-iteration costs.** `hot-path.md` forbids threshold
  relaxation; the wait path is gated with the exact `Assert.Equal(0, allocated)`.
