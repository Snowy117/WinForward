# Implementation notes — F5 pump I/O shape (read shape + idle wake)

Task `.trellis/tasks/10-01-pump-io-shape` (finding F5 of the archived
`09-29-tcp-udp-path-structural-perf`). Every anchor below was read on tree `56dd85f`
(`git write-tree` `6599093d8033ce2b624a0b110c4fb2a65aa41d5f`), **not** copied from the research's
sketch. Where the sketch and the code disagree, the code wins and the disagreement is in §8.

These notes answer, in order: the exact read path (§1), what the queue query is for and what the
batched read actually returns (§2), how the pump paces idle and where the sleep lives (§3), which
read-call counting seams exist and what they miss (§4), the disposal/cancellation contract around a
blocking wait (§5), which seams are already injectable vs must be created (§6), the reproduce-before
commands with the recorded numbers (§7), the discrepancy register (§8), and what this host cannot
establish (§9).

---

## 1. The exact current read path

### 1.1 `NdisApiDriver.TryReadPackets` (`src/WinForward.NdisApi/NdisApiDriver.cs:136-166`)

| Line | What happens |
|---|---|
| `:144-145` | `ArgumentNullException.ThrowIfNull(buffers)`; `buffers.Length == 0` returns 0 (parameter validation, no native call) |
| `:153` | `_adapterGates.Get(adapterHandle)` — `ConcurrentDictionary.GetOrAdd` on the per-adapter gate map (`NdisNativeCallGate.cs`), lock-free after the first call |
| `:154` | one `NdisNativeCallGate` lease spans **both** native calls below |
| `:156` | **IOCTL #1** `NdisApiNative.GetAdapterPacketQueueSize(_handle, adapterHandle, &queuedPacketCount)` (`NdisApiAbi.cs:208-210`) |
| `:157` | `queueError = queueResult == 0 ? Marshal.GetLastWin32Error() : 0` — last-error is captured immediately after the call, inside the lease |
| `:158` | `NdisNativeCallStatus.HasQueuedPackets(...)` — throws `Win32Exception` on a failed query (`NdisNativeCallStatus.cs:17-21`), otherwise returns `queuedPacketCount != 0` |
| `:160` | `requestedCount = min(queuedPacketCount, buffers.Length)` — **the query sizes the request** |
| `:161` | **IOCTL #2** `ReadPacketsBatch(...)` — only when the query said non-empty |
| `:165` | `InterpretBatchReadResult(queuedPacketCount, requestedCount, readResult, readError, packetsSuccess, adapterHandle)` |

`ReadPacketsBatch` (`:170-188`) stack-allocates the `ETH_M_REQUEST` when it fits
(`MultiRequestByteCount(count) <= MaxStackMultiRequestBytes` = 1024, `:25`), otherwise
`NativeMemory.AllocZeroed`/`Free` — the heap path is unreachable for the pump's 32-slot batch
(16 + 8·32 = 272 B) and exists for larger caller arrays. `ReadPacketsRequest` (`:190-198`) builds the
request, calls `NdisApiNative.ReadPackets`, and captures `nativeError` and `request->PacketsSuccess`
immediately after. `BuildMultiRequest` (`:202-214`) is `internal static` (shared with the batched
**send** path at `:279`) and also fills `PacketsSuccess = 0` before the call.

Call shape per drain, today: **non-empty queue → 2 IOCTLs (query + read); empty queue → 1 IOCTL
(query only), and the read is never issued.** Under load every batch pays the query; at idle every
adapter pays one query per poll cycle. That is F5.1's evidence, confirmed at the source.

### 1.2 Every caller

`TryReadPackets` reaches the driver through exactly one abstraction,
`INdisPacketReader` (`NdisCapture.cs:22-29`). The interface has **twelve implementations** in the tree
— `NdisApiDriver.cs:23` (production) plus eleven doubles
(`rg -n ': INdisPacketReader' tests/ benchmarks/`): `FiniteCaptureReader`
(`benchmarks/…/Perf/CapturePumpBenchmarks.cs:66`), `GatedEmptyCaptureReader` and
`SignallingCaptureReader` (`benchmarks/…/Stability/PumpIdleWakeScenario.cs:372/417`), `GatedReader`
(`tests/…/NdisCapturePumpTests.cs:377`), `PerHandleReader` / `PermanentFailureReader` /
`CancellingReader` (`tests/…/NdisCaptureResilienceTests.cs:264/269/274`), `ScriptedReader` /
`CountingCaptureReader` (`tests/…/TestHelpers/ScriptedReader.cs:12/30`), `FiniteMixedPassReader` /
`ScriptedFaultingReader` (`tests/…/BatchedPassReinjectionE2eTests.cs:137/166`).

That count is the reason the arrival-signal seam is **not** added to `INdisPacketReader`: widening the
read interface would touch all twelve, while two optional `NdisCapturePumpOptions` members touch none
(`design.md` §4.1).

| Caller | Site |
|---|---|
| `NdisCapturePump.RunIteration` | `NdisCapture.cs:269` — the only production call, once per loop iteration |
| the eleven doubles | see the enumeration above |

There is no other caller of `NdisApiDriver.TryReadPackets` in `src/`, `tests/` or `benchmarks/`.
**The pump never changes for F5.1**: the whole read-shape change is inside
`NdisApiDriver.TryReadPackets`.

### 1.3 The gate topology the read shape must preserve

`windows-ndisapi.md:86` pins it: the query + batch read pair shares **one** lease of the adapter's
gate, the map is never held across `gate.Enter()`, and the control gate is never nested inside an
adapter gate. `TryReadPackets:153-154` is the current instance of that rule. A read-first shape keeps
the same single lease; it only changes which calls happen inside it.

---

## 2. What the query is used for, and what the batched read returns

### 2.1 The three jobs the query does today

1. **Skip the read on an empty queue** (`:158`) — the loop's idle branch is
   `requestedCount == 0 ⇒ readResult == 0 ⇒ InterpretBatchReadResult` returns 0 at
   `NdisNativeCallStatus.cs:25`.
2. **Size the request** (`:160`) — `dwPacketsNumber = min(queued, capacity)`.
3. **Distinguish "empty" from "error"** (`NdisNativeCallStatus.cs:23-33`) — `queuedPacketCount == 0`
   short-circuits to 0 *before* the read result is examined; a failed read with a non-empty queue
   (`:26-29`) throws.

### 2.2 Pinned ABI declarations

`ETH_M_REQUEST` (Pack=1, x64) = `hAdapterHandle(8) + dwPacketsNumber(4,in) + dwPacketsSuccess(4,out)
+ NDISRD_ETH_Packet[N]` (one `INTERMEDIATE_BUFFER*` each) = 16 + 8N, asserted by
`NdisApiAbi.AssertManagedX64Layout` (`NdisApiAbi.cs:22-55`); managed mirror
`EthernetMultiRequest` (`:119-126`, `FirstBuffer` aliases `EthPacket[0]`).

| Native declaration | Code anchor | Pinned upstream evidence |
|---|---|---|
| `BOOL GetAdapterPacketQueueSize(HANDLE hOpen, HANDLE hAdapter, DWORD* pdwSize)` | `NdisApiAbi.cs:208-210` | `IOCTL_NDISRD_ADAPTER_QUEUE_SIZE`, in→adapter handle (4 B), out→queue size (4 B) |
| `BOOL ReadPackets(HANDLE hOpen, PETH_M_REQUEST)` | `NdisApiAbi.cs:212-214` | `wiresock/ndisapi@417b8734` `include/ndisapi.h:302` (C wrapper), `ndisapi.vs2012/ndisapi.def:11` (export), `ndisapi/ndisapi.cpp:872-965` (`CNdisApi::ReadPackets`) |
| `BOOL SetAdapterListChangeEvent(HANDLE hOpen, HANDLE hEvent)` | `NdisApiAbi.cs:196-198` | same commit, already wired |
| **`BOOL SetPacketEvent(HANDLE hOpen, HANDLE hAdapter, HANDLE hWin32Event)`** | **absent from the tree** | `include/ndisapi.h:307` (C wrapper), `ndisapi/ndisapi.cpp:3586`; `.def:16` export; **hardware-verified** bare-name export on the real DLL: `.trellis/tasks/archive/2026-08/08-27-fix-datapath-throughput/implement.md:150-159` (134 exports parsed on the Windows smoke host, 2026-08-27) |

The `SetPacketEvent` **signature is pinned** (3 parameters, order `hOpen, hAdapter, hWin32Event`,
`__stdcall`, `BOOL`); only its *runtime behaviour* is unverifiable here. Official remarks
(ntkernel.com "SetPacketEvent", C API page): "The user application should create a Win32 event (with
`CreateEvent` API call) and pass adapter handle and event handle to this function. Helper driver will
signal this event when the adapter associated packet queue is non-empty." Passing `NULL` releases the
registration — the same release contract already used for `SetAdapterListChangeEvent`.

### 2.3 What `ReadPackets` returns on an empty queue: all available evidence

The user-mode layer is a **pure passthrough**: `CNdisApi::ReadPackets`
(`ndisapi.cpp:872-965`) is `DeviceIoControl(IOCTL_NDISRD_READ_PACKETS, …)` and `return bIOResult;` —
no empty-queue special case, no `dwPacketsSuccess` post-processing on the non-WOW64 branch
(`:950-965`). The
empty-queue semantics therefore live entirely in the **closed-source kernel driver** (`ndisrd.sys`;
`NdisNativeCallStatus.cs:38-39` already records that the driver is closed-source).

| Source | What it says | Strength |
|---|---|---|
| Official `ReadPackets` docs (C API + C wrapper pages) | "TRUE if call was successful, FALSE otherwise"; `dwPacketsSuccess` is filled "in case of success". **Silent on the empty queue.** | negative result — the documented contract does not answer the question |
| Pinned user-mode wrapper | passthrough; FALSE ⟺ `DeviceIoControl` failed | bounds the question to the kernel driver |
| Vendor .NET sample `examples/dotNet/TestDotNet/Program.cs:275-323` | `var packetList = ReadPackets(...); while (packetList.Item1) { …; packetList = ReadPackets(...); }` — `Item1` is the **raw BOOL** (`ndisapi.net/ndisapicl.cpp:139-172` returns `Tuple::Create(true|false, …)`); then `packetEvent.Reset()` once the loop exits | **strong inference: FALSE = queue drained** |
| Vendor C++ samples `examples/cpp/common/ndisapi/queued_packet_filter.h:481-487`, `simple_packet_filter.h:330-332` | `do { wait_event(INFINITE); reset_event(); } while (!ReadPackets(read_request) && running);` | same inference, retry-shaped |
| Vendor samples `network_adapter.h:146`, `TestDotNet/Program.cs:58` | `CreateEvent(nullptr, TRUE, FALSE, nullptr)` / `new ManualResetEvent(false)` — **manual-reset**, explicitly reset after each wait | behaviour of the *event*, not the read |
| This estate's own reasoning | `windows-ndisapi.md:424` "Queue empty (`queuedPacketCount == 0`) → `TryReadPackets` returns 0"; the read is guarded, so the estate has never observed the empty read | no evidence either way |

Why the sample is *evidence* and not proof: a `TRUE`-with-`dwPacketsSuccess == 0` driver would make
`while (packetList.Item1)` spin forever at 100 % CPU, so the vendor's own shipped sample cannot
survive that ABI. That is an inference from a sample, not a specification, and the kernel handler is
not readable here. **Conclusion: the pinned ABI cannot be shown, on this host, to distinguish
"empty" from "error" in the read result** — which is exactly the condition PRD requirement 1 names.

### 2.4 The status-classification helpers and their call sites

`NdisNativeCallStatus` (`NdisNativeCallStatus.cs`) is the single classification site:
`HasQueuedPackets:17-21` (throw on a failed query, else `count != 0`), `InterpretBatchReadResult:23-33`
(empty short-circuit, throw on a failed read from a non-empty queue, otherwise clamp
`packetsSuccess` to `requestedCount`), `IsTransientReadError:48-54` (21 / 170 / 1237 / 995 / 1167 /
31, everything else permanent — fail-closed conservative).

Direct callers: `NdisApiDriver.cs:158` and `:165` (production) and `NdisApiAbiTests.cs:50-53`
(`HasQueuedPackets`), `:62-77` (`InterpretBatchReadResult`). Both facts pin the classifier, not the
driver, so the driver's ordering change is not covered by any existing test — that gap is the
instrument Step 1 exists to close.

**Consequence for a read-first shape:** `InterpretBatchReadResult`'s `queuedPacketCount == 0 ⇒ 0`
short-circuit is unreachable once the query stops running first, and its `nativeResult == 0 ⇒ throw`
branch would throw on a `FALSE`-on-empty driver. The helper must be re-ordered, not just re-called —
see `design.md` §2.

### 2.5 Two consequences of requesting the full batch capacity

Today the request is sized by the query (`min(queued, capacity)`, `:160`), so it never exceeds what the
driver just said was queued. A read-first shape has no queue knowledge and requests `buffers.Length`
(32 for the pump), which introduces two behaviours that do not exist today:

1. **Short-count success must hold (PRD requirement 7, acceptance-relevant).** If the driver fails a
   read whose `dwPacketsNumber` exceeds the queue depth, then a queue holding 3 packets produces
   `FALSE` → the disambiguating query reports 3 → the classifier throws `readError` → if that error is
   in `IsTransientReadError` (`NdisNativeCallStatus.cs:48-54`) the pump retries five times with backoff
   and then **degrades a perfectly working adapter while packets are available**. This cannot happen in
   today's shape, which is why it is a new open item with the query-first rollback as its fallback —
   not a tuning knob. Evidence that it *should* hold: the pinned docs say `dwPacketsSuccess` "contains
   number of packets returned by driver", and every vendor sample requests the full buffer capacity
   (`examples/dotNet/TestDotNet/Program.cs:275-323` with `NdisBufferResource(64)`,
   `ndisapi.net/ndisapicl.cpp:139-172`); the docs are silent on the queue-depth relation, so it stays
   an on-Windows experiment (`design.md` §9 row 1).
2. **A null hole in the caller's array now throws on every drain, not only on a non-empty queue.**
   `BuildMultiRequest` (`NdisApiDriver.cs:202-214`) walks slots `[offset, offset+count)` and throws
   `ArgumentNullException` on a null entry; with a full-length request that walk always covers the whole
   array. Today `windows-ndisapi.md:427` is literally "`buffers` empty or contains null entries |
   0 / `ArgumentNullException`" — the `0` half only because an empty queue returned before the request
   was built. The pump's batch buffers are never null (`NdisCapture.cs:143-144`), so this is an
   ABI-layer contract change, not a data-path change; it joins `design.md` §2.4's table.

---

## 3. How the pump paces idle, and where the sleep lives

| Element | Anchor |
|---|---|
| Default poll delay, `TimeSpan.FromMilliseconds(1)` | `NdisCapture.cs:142` (`_pollDelay = options?.PollDelay ?? …`) |
| Option that overrides it | `NdisCapture.cs:40-41` (`NdisCapturePumpOptions.PollDelay`) |
| The empty-queue branch | `NdisCapture.cs:286-297`: `readCount == 0` → `_onBatchCompleted?.Invoke()` (`:290`) → `cancellationToken.ThrowIfCancellationRequested()` (`:293`) → `_stopped` check (`:294`) → `PaceIdle()` (`:295`) |
| **The sleep itself** | `NdisCapture.cs:312-318`: `private void PaceIdle() => Thread.Sleep(_pollDelay);` (synchronous, allocation-free, deliberately not `Task.Delay`) |
| The loop | `RunLoop:197-247` → `while (ShouldContinue(token))` `RunIteration` (`:264-310`); one dedicated background `Thread` per pump (`:169-175`) |
| Timer resolution | `WinForward.Windows.HighResolutionTimerScope` (winmm `timeBeginPeriod(1)`, fail-open one-shot warn) is wrapped around the capture run by the Cli; `windows-ndisapi.md:98` records that it makes the 1 ms delay resolve to ~1–2 ms |

The pump's *other* blocking waits: the transient-retry backoff `SleepInterruptible`
(`NdisCapture.cs:370-380`, bounded by `TransientRetryDelay` ≤ 1.6 s and token-interruptible via
`cancellationToken.WaitHandle.WaitOne(delay)`), and `InvokeHandler`'s deliberate block on a pending
handler (`:327-346`). The drain-till-empty behaviour is **not** a nested loop: each iteration reads
one batch and the outer `while` re-reads with no wait whenever the batch was non-empty
(`:299-309`), so a burst drains at full speed and the wait is only ever reached after a read returned 0.

---

## 4. The existing read-call counting seams, and what they do not cover

| Seam | Where | What it counts | What it cannot see |
|---|---|---|---|
| `INdisPacketReader.TryReadPackets` + `CountingCaptureReader` | `tests/…/CapturePumpReadCallTests.cs:25-72` | exactly one read call per loop iteration; 6 reads / 7 packets over a scripted 6-iteration run; the returned-count sequence `[0,0,4,3,0,0]`; `EmptyReads`=4 / `BatchReads`=2; one `OnBatchCompleted` per iteration + 1 on exit | the query+read **pair inside** `NdisApiDriver.TryReadPackets` — it is below this seam by construction |
| `OnBatchCompleted` counter | `CapturePumpReadCallTests.cs:141-146`, used as the poll counter by `PumpIdleWakeScenario.cs:69/74` | one increment per iteration (+1 on run exit) | same limit; it counts *iterations*, not IOCTLs |
| Benchmark rows `pump.idle` / `pump.idleWake` | `PumpIdleWakeScenario.cs:66-125` / `:169-205`; recorded in `benchmarks/results/2026-09-29-benchmark-coverage/pump-idle-wake.jsonl` | `readCalls`, `polls`, `readCallsPerPoll`, `readCallsPerPacket`, `emptyReads`, 0 B gate, CPU/idle-second, arm→dispatch percentiles | same limit; the README states it explicitly at `benchmarks/results/2026-09-29-benchmark-coverage/README.md:327-333` ("The caveat the F5.1 half inherits: the driver's own IOCTL pair is below this seam") |
| Driver-side telemetry | **none** | — | nothing on the read path is counted at the driver level at all |
| **New driver counters** (this task; `NdisApiDriver.ReadDiagnostics`) | increment sites in `NdisApiDriver.TryReadPackets`: `BatchReads` once per read call; `QueueSizeQueries` once per query call; `FailedReads` once per **non-success read, before classification**; `EmptyReads` once per drain whose **classified** result is 0 (read-success-0 **or** read-fail-then-query-0); `LastFailedReadNativeError` on every non-success read | the four counters plus the last error | **seam-level call shape only**, never kernel round trips; the A-vs-B discrimination depends on `FailedReads` covering every non-success read *before* classification (review MAJOR 3) |

**The gap F5.1 must close:** there is no instrument between `NdisApiDriver.TryReadPackets` and
`NdisApiNative`. `ReadPackets` and `GetAdapterPacketQueueSize` are static `[LibraryImport]` calls on a
type whose static constructor loads `ndisapi.dll` from `AppContext.BaseDirectory`
(`NdisApiAbi.cs:155-178`), so no test can drive the driver's own logic today — `NdisApiDriver.Open()`
throws `DllNotFoundException` on this host, and there is no `NdisApiDriver` test anywhere in the tree.

The PRD's phrase "a counting seam at the pump↔driver boundary" is therefore two boundaries: the
pump↔driver half **already exists** (`CapturePumpReadCallTests`) and is untouched by F5.1; the half
requirement 2 needs is **driver↔native**, and it must be created.

---

## 5. The disposal / cancellation contract around a blocking wait

The current contract (`NdisCapture.cs:59-94` class doc, `:406-423` `DisposeAsync`):

- `DisposeAsync` sets `_stopped` (`:419`) and, when a run started, parks on `_runCompletion`
  (`:420`) — the source the run's exit sequence completes **after** `ReleaseBatchBuffers` (`:229-240`).
- The run is never cancelled by disposal: the loop observes `_stopped` every iteration (`:294`), and
  "its only waits are bounded by the configured poll delay, a single transient-retry backoff sleep, or
  an in-flight handler" (`:410-414`).
- `RunIteration` checks the token **and** `_stopped` *before* `PaceIdle()` (`:293-295`), so a stop that
  arrives around the read adds no poll-delay latency to the exit.
- Dispose before any run releases the batch buffers synchronously (`:420-422`); the release is
  single-shot (`:425-429`).

**What a blocking arrival wait changes:** the idle wait's upper bound stops being `PollDelay` (1 ms,
off the hot path) and becomes the arrival timeout. The PRD pins that bound ("the disposal/cancellation
contract must stay bounded by the timeout"), so the design must (a) choose the timeout with the
generation-stop path in mind, (b) keep the `_stopped`-before-wait ordering, and (c) state the bound
correctly — it is **not** simply "one timeout", because the two disposal paths differ:

- **Production (`StopAsync`, the only path the runtime takes)**: `TransactionalCaptureRuntime.StopAsync`
  (`CaptureLifecycle.cs:108-135`) cancels `_scope` (`:126`) **before** awaiting the run task (`:129`),
  and the run body's `finally` (`:83-87`) runs `CleanupAsync` — which disposes the capture loop — only
  after `_capture.RunAsync` returned. Every parked pump therefore wakes from its own timeout
  **concurrently**, so the wall-clock cost is ~one timeout for all adapters, and the sequential
  `await pump.DisposeAsync()` loop in `MultiAdapterCaptureLoop.DisposeCoreAsync:99-107` then finds
  each run already finished. Same ordering through `RuntimeCaptureGeneration.DisposeAsync` →
  `_runtime.DisposeAsync()` → `StopAsync()`.
- **Direct loop disposal** (tests, or any future caller that disposes the loop without cancelling
  first): each `await pump.DisposeAsync()` sets that pump's `_stopped` and waits for its *own*
  remaining timeout, so the bound is **N × timeout**. No production path does this today.

---

## 6. Seams: already injectable vs to be created

| Need | Status | Anchor / gap |
|---|---|---|
| Pump read seam | **exists** | `INdisPacketReader` (`NdisCapture.cs:22-29`); twelve implementations already (§1.2) — widening it would touch all of them, which is why the new seam goes into the options record |
| Pump options record with public knobs + `internal init` test seams | **exists** | `NdisCapturePumpOptions` (`NdisCapture.cs:38-57`); the doc at `:34-36` ("the two internal members …") must be updated when public knobs are added |
| Single-iteration pump seam (deterministic idle-path measurement) | **exists** | `NdisCapturePump.RunIterationForTests` (`:182-188`, `internal`) |
| Pump batch-capacity / retry-base overrides | **exists** | `:52-56` (`internal init`) |
| Pump **stop** seam | **exists** | `DisposeAsync` + `_stopped`; no handle to wake a parked wait |
| Driver↔native read calls (`GetAdapterPacketQueueSize` / `ReadPackets`) | **must be created** | static `[LibraryImport]` only; `TryReadPackets` is untestable |
| Driver test construction (no `ndisapi.dll`) | **must be created** | `NdisApiDriver`'s ctor is private (`:35`) and `Open()` (`:37-58`) needs the DLL + `AssertManagedX64Layout`; `CreateForTests` with `NdisApiSafeHandle.FromRawHandle(0)` keeps `Dispose` off `ReleaseHandle` (0 is invalid for `SafeHandleZeroOrMinusOneIsInvalid`), so no test trips the DLL resolver |
| Read-call counters (production, observable on Windows) | **must be created** | no read telemetry exists (§4); `FailedReads` must increment on every non-success read *before* classification or the `design.md` §9.2 A-vs-B discrimination is unsound |
| Driver read-path byte gate | **must be created** | constructible on this host via `CreateForTests`; no gate exists because no test could reach the body before |
| Read-shape self-heal (guard flag, probe, diagnostic sink) | **must be created** | no such state exists; the guard lives on the driver keyed by adapter handle (`design.md` §3.5) because the call shape is the driver's and `INdisPacketReader` must not be widened; the sink is public because `WinForward.Runtime` is not a friend assembly (precedent: `NdisPacketBufferPool.Shared.AccountingSink`) |
| Request builder (`BuildMultiRequest` / `MultiRequestByteCount` / the 1 KB stack budget) | **stays put** | three facts call `NdisApiDriver.BuildMultiRequest` (`NdisApiBatchedSendAbiTests.cs:36/60/79`), so the helpers remain `internal static` on the driver and the read-calls class calls them (notes D13, corrected 2026-10-01) |
| Packet-arrival wait (injectable) | **must be created** | `SetPacketEvent` is not even declared (§2.2); the pump has no idle-wait seam. The interface goes beside its implementation in `NdisPacketArrivalSignal.cs` (`directory-structure.md:66`), not into the consumer's file |
| Packet-arrival **event binding** (create event, register, release on dispose) | **must be created** | the only precedent is `NdisAdapterListWatcher` (`AdapterListWatcher.cs:37-96`): `EventWaitHandle(initialState: false, EventResetMode.AutoReset)` (`:52`), raw handle handed to the driver (`:44-46`), NULL-release + event dispose on `Dispose` (`:77-96`) |
| Composition site that can own a per-generation, per-adapter binding | **exists, must be extended** | `NdisCaptureGenerationFactory.Create` (`NdisCaptureGeneration.cs:103-137`, loop at `:132-134`) + `MultiAdapterCaptureLoop` ctor (`MultiAdapterCaptureLoop.cs:35-54`) + `DisposeCoreAsync` (`:99-107`) |
| Loop-level test construction without hardware | **exists** | `NdisCaptureResilienceTests.cs:200-208` builds a real loop with a pass configuration + `NoopExecutor` |

---

## 7. Reproduce-before commands and the recorded numbers

Tree `56dd85f`. Nothing below was re-run while planning (read-only session); the numbers are the
recorded ones from `benchmarks/results/2026-09-29-benchmark-coverage/README.md:275-330` and
`pump-idle-wake.jsonl` (three `--quick` runs, this host: AMD Ryzen 9 9955HX, NixOS 26.11, .NET 10.0.12,
Release).

```bash
# 0. tree identity + suite baseline
git rev-parse --short HEAD && git write-tree
dotnet test WinForward.slnx -c Release            # F4 archive recorded 1,057 + 18

# 1. the F5 baseline series (already on disk). Never point --output at the archived file: the runner
#    truncates its output per process, which would destroy the recorded baseline. Write to /tmp and diff.
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario pump --quick \
  --output /tmp/wf-f5-baseline-recheck.jsonl

# 2. the exact gates that must not move (per-class totals; hot-path.md:1303)
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~CapturePumpReadCallTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisCapturePumpTests"
dotnet test WinForward.slnx -c Release --filter "FullyQualifiedName~NdisApiAbiTests"

# 3. the driver-internal call shape has NO instrument today — that is Step 1's deliverable
rg -n "GetAdapterPacketQueueSize|NdisApiNative.ReadPackets" src/WinForward.NdisApi/NdisApiDriver.cs
```

Recorded baseline (three runs, exact where the row says exact):

| Reading | run 1 | run 2 | run 3 | Kind |
|---|---:|---:|---:|---|
| `pump.idle` window / polls / polls-per-second | 15.001 s / 13,165 / 877.6 | 15.001 s / 13,284 / 885.6 | 15.001 s / 13,288 / 885.8 | series |
| `cpuSeconds` / `cpuSecondsPerIdleSecond` | 0.267 / **0.017798** | 0.2775 / **0.018501** | 0.2674 / **0.017826** | series |
| `allocatedBytes` (row's own gate) | **0** | **0** | **0** | exact |
| `readCallsPerPoll` (pump seam) | **1.0** | **1.0** | **1.0** | exact |
| `pump.idleWake` p50 / p95 / p99 / max (ms) | 0.0779 / 0.1268 / 0.1890 / 0.5844 | 0.0783 / 0.1239 / 0.1877 / 0.8211 | 0.0791 / 0.1221 / 0.1662 / 0.7125 | series (report-only) |
| `readsPerWake` / `readCallsPerPacket` / `emptyReads` | 1.0 / 1.0 / 1 | 1.0 / 1.0 / 1 | 1.0 / 1.0 / 1 | exact |
| gate class totals | `HotPathAllocationGateTests:11 CapturePumpReadCallTests:3 SweepAllocationGateTests:12 NdisCapturePumpTests:14` | | | exact (F4 `class-totals.txt:13`) |

**Driver-internal before-counts (derived from §1.1, not measured):** query+read = **2 IOCTLs per
non-empty drain**, **1 query per empty poll**, **0 reads on an empty queue**. Because the pump seam
reports one read per poll and the driver's pair is invisible, these three numbers are what the new
instrument must make exact.

Reading conventions that bind the after-series (`benchmarks/results/2026-09-29-benchmark-coverage/README.md`
"Readings", `hot-path.md` §"Allocation-gate stability"): **counts and bytes are gates; timing is a
series**. The idle CPU baseline is explicitly attributed to *`Thread.Sleep` resolution, not pump
work* — ~880 polls/s × ~20 µs (timer arming + two context switches) — which is the quantity F5.2
removes.

---

## 8. Discrepancy register (research/PRD vs code; code wins)

| # | Claim in the input documents | What the code says | Effect on the design |
|---|---|---|---|
| **D1** | PRD `prd.md:15-17`: "the driver exposes batched-*send* telemetry only — so the write side's counters cannot stand in for a read-call proof"; `windows-ndisapi.md:278` names `NdisApiDriver.BatchedSendFlushCount` / `BatchedSendPacketCount` | The driver exposes **no** telemetry at all: `rg -ni 'flushcount\|packetcount' src/` finds neither member (the nearest counters are `TcpRedirectDiagnostics.RedirectDegradedFlushCount` and `NdisPacketActionExecutor.ImmediateSendLaneOverflowCount`). The spec row is stale | The requirement stands and is *stronger* (there is nothing to borrow); Step 4 must correct `windows-ndisapi.md:278` or restore the counters |
| **D2** | Research F5.1 (`research.md:242-245`): "a shape `NdisNativeCallStatus.InterpretBatchReadResult` already handles" | The helper's first statement is `if (queuedPacketCount == 0) return 0;` and its next is `if (nativeResult == 0) throw` — with the query dropped the first is unreachable and the second fires on a `FALSE`-on-empty driver | The classifier is re-ordered in the same step as the shape (§`design.md` §2.3); the existing facts at `NdisApiAbiTests.cs:58-78` are rewritten, not deleted |
| **D3** | PRD req 2: "a counting seam at the pump↔driver boundary" | The pump↔driver seam exists and is gated (`CapturePumpReadCallTests`); the query/read pair is one layer lower (driver↔native, `NdisApiDriver.cs:156/161`) | One new seam is created at the driver↔native boundary; the pump-level counter is kept as the outer half and must stay green |
| **D4** | PRD req 3: "the pump waits on the driver's packet-arrival signal (the ndisapi event binding)" | No `SetPacketEvent` declaration exists anywhere in the tree (`NdisApiAbi.cs` has 13 `LibraryImport` declarations, none of them it); `windows-ndisapi.md:98` still calls event-driven reads "the optional next upgrade" | The P/Invoke is added and pinned from `417b8734` (§2.2); the *behaviour* is a Windows open item (§9) |
| **D5** | `windows-ndisapi.md:403`: "`TryReadPackets` — queue query + batched read merged into a single `NdisNativeCallGate` lease; returns the driver-filled success count (0 = empty queue)" and the matrix row `windows-ndisapi.md:425`: "`ReadPackets` returns FALSE with a non-empty queue → throw" | True today; both statements become wrong under a read-first shape (the query is no longer part of the steady-state pair; a `FALSE` read is classified through the query) | F5 owns these two spec rows (§`design.md` §11) |
| **D6** | `windows-ndisapi.md:98`: "The 1 ms poll delay remains only on empty batches … `SetPacketEvent` event-driven reads are the optional next upgrade if empty-to-first-packet latency still matters" | Accurate today (`NdisCapture.cs:295/318`); the measured 1.78 % CPU per idle second and the recorded 0.078 ms wake proxy are the case for the upgrade | F5 makes the event the primary idle path and the sleep the fallback; the row is rewritten |
| **D7** | Research F5.2: "`WaitForSingleObject(event, timeout)`; on wake, drain-till-empty, then re-arm" | There is no "re-arm" step today and none is needed: the pump's outer loop re-reads unconditionally, and the wait is entered only after a read returned 0 (`NdisCapture.cs:286-297`) | The design keeps the existing loop shape and adds the wait *in place of* `PaceIdle()`; "drain-till-empty" is the existing outer loop, unchanged |
| **D8** | `NdisCapturePumpOptions` doc (`NdisCapture.cs:34-36`): "The two `internal` members are test and benchmark seams …" | Two internal members exist (`:52-56`); adding public knobs changes the sentence's arithmetic and its meaning | Doc updated in the same commit; the new public knobs are production-injectable, the new timeout is a documented constant-with-override |
| **D9** | Vendor reference shape: manual-reset event + explicit `Reset()` after the wait (pinned samples, §2.3) | The estate's own analogue (`NdisAdapterListWatcher.cs:52`) uses an **auto-reset** event, documented as "coalesces signal bursts into one pending observation … a driver signal raised while no waiter is parked stays set, so nothing is lost" (`:22-27`) | The design chooses auto-reset and states why the sample's manual-reset + reset-after-wait has a lost-wakeup window the auto-reset shape does not (see `design.md` §4.4); reviewers should attack this deviation |
| **D10** | Research F5.1 measurement: "Halves IOCTLs under load; idle cost is unchanged (one IOCTL either way)" | Idle cost is *not* unchanged under a naive read-first shape: a `FALSE`-on-empty driver pays read+query instead of one query | The 2-IOCTL idle poll is accepted because F5.2 cuts the idle *poll count* by ~90×; the interaction is recorded in `design.md` §2.5 |
| **D11** | PRD AC1, original text: "the empty-queue case still yields zero packets with no error and **no extra call**" | Under the documented-by-inference `FALSE`-on-empty ABI, the only safe read-first shape pays one disambiguating query on that path | **Resolved by the 2026-10-01 PRD amendment**, which now reads "the residual query is permitted **only** on the non-success path, where the fail-closed equivalence table must hold state-for-state" (`prd.md:60-63`) — exactly this design. `design.md` §2.4 keeps the state table as the discharge |
| **D12** | `hot-path.md:1303` per-gate totals string: `… CapturePumpReadCallTests:3 … NdisCapturePumpTests:14` | Adding idle-wait facts to either class changes the string; the spec instructs to refresh it "whenever the suite grows" (`:1295-1297`) | Step 5 refreshes the string; the proof loop's `totals=` variable is part of the diff |
| **D13** | (planning error, **corrected 2026-10-01 by independent review**) `NdisApiDriver.BuildMultiRequest` (`:202-214`) "has no test caller, so the helper can move with the read path" | **Three facts call it**: `NdisApiBatchedSendAbiTests.BuildMultiRequestFillsHeaderAndSlotsInOrder` (`tests/…/NdisApiBatchedSendAbiTests.cs:36`), `BuildMultiRequestOffsetSelectsTheSuffixBuffers` (`:60`), `BuildMultiRequestRejectsNullEntriesInsideTheRange` (`:79`) — 3 of that class's 4 facts. `rg -n 'BuildMultiRequest' tests/` finds them; the original sweep grepped only `MaxStackMultiRequestBytes`/`MaxPacketsPerSendRequest` | **`BuildMultiRequest` and `MultiRequestByteCount` stay `internal static` on `NdisApiDriver`**; the read-calls class calls them and `MaxStackMultiRequestBytes` widens to `internal const` (its only remaining in-class use is deriving `MaxPacketsPerSendRequest`, `:29`). The null-slot promise of `windows-ndisapi.md:427` therefore keeps a reachable guard, and §2.5 item 2 records that the read path now reaches it on every drain |

---

## 9. What this host cannot establish (recorded, never assumed)

No Windows driver, no `ndisapi.dll` (`NdisApiAbi.cs:155-178` loads it from
`AppContext.BaseDirectory`; there is no sidecar on this host), and the kernel side is closed-source.

1. **The ABI-mismatch row: whether a read requesting the full batch capacity succeeds with a short
   count** when the queue holds fewer packets than requested (§2.5 item 1; PRD requirement 7 makes this
   **acceptance-relevant**, so its row must appear in the artifact README). Evidence points both ways:
   the docs describe `dwPacketsSuccess` as "number of packets returned", and every vendor sample requests
   the full buffer capacity — but the docs never state the queue-depth relation, and the pre-change code
   has always requested `min(queued, capacity)`, so the estate has no observation either way.
   **Handling: the self-healing guard (`design.md` §3.5) — a first `read failed + query > 0` observation
   arms a sticky per-handle query-first shape, logs `adapter.readShape.mismatch`, and retries the drain
   with one extra read, so a mismatching ABI self-corrects instead of degrading a healthy adapter.** The
   experiment therefore only *confirms which shape the hardware takes*: **no `adapter.readShape.mismatch`
   line** after a long run means the read-first shape is accepted, exactly one line per adapter means it
   was healed (the warn carries `requested`/`queued`/`nativeError`) and the drain shape is
   `[Query, Read]` from then on. The counter (`NdisReadDiagnostics.ReadShapeMismatchCount`) is `internal`,
   so it is the seam tests' instrument and a future heartbeat's input — a production Windows run reads
   the log. Residue: none on a conforming
   driver (the guard never fires); on a mismatching one, one extra query per non-empty drain for that
   adapter until the next generation. If the guard itself ever fails to hold (a second warn for the same
   handle, or a degradation), the query-first rollback of `implement.md` Step 2 applies — **not** a
   tuning knob. Sequencing constraint for the experiment: run the idle half **first**, because the guard
   cannot arm on an empty queue and arming would change the counters item 2 reads.
2. **Which empty-queue ABI hypothesis the real driver implements** — `ReadPackets` returning `TRUE`
   with `dwPacketsSuccess == 0` on an empty queue, or `FALSE` (and with which last-error). §2.3's sample
   inference points at `FALSE`, which is precisely why the design must be safe under both.
3. **Whether the driver's `SetPacketEvent` signal is per-enqueue, per empty→non-empty transition, or
   level-ish**, and therefore whether a single signal can be lost between the pump's empty read and
   its wait. The bounded timeout is the design's answer; only hardware can measure the residual.
4. **The wake cost on real hardware** (`KeSetEvent` → user-mode wait return → IOCTL), and the
   end-to-end arrival→dispatch latency on a real NIC. The recorded 0.078 ms is a *Linux semaphore*
   proxy from `PumpIdleWakeScenario.cs:417-470`; the new arm measures the production wait path but
   still on this host's `EventWaitHandle`, not on a Windows driver event.
5. **Real IOCTL counts** — the instrument counts *call shape* (which calls the driver makes, in what
   order), never kernel round trips; the query/read split below `NdisApiNative` is unobservable here.
6. **The generation-stop latency** with a pump parked in the arrival wait on a real adapter set, for
   both disposal orderings of §5 (the production cancel-first path ≈ one timeout; a direct loop
   disposal is N × timeout and has no production caller today).

Each becomes a row of the Windows-open-items table with its experiment, expected observation and
fallback (`design.md` §9); none is silently converted into an assumption. Items 1–2 map to design rows
1–3 (the design table separates the empty-queue hypothesis from the error-code question), items 3–4 to
rows 4–6, and items 5–6 to rows 7 and beyond. Items 1 and 2 are the two that can force the query-first
rollback; the rest are measurements with a documented fallback each.
