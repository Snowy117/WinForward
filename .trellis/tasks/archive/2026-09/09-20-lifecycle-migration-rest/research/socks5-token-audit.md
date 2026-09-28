# Research: SOCKS5 token/disposal audit (C4)

- **Query**: every `CancellationTokenSource` created/disposed and every `.Token`/`IsCancellationRequested` read in `Socks5ControlConnection.cs` and `Socks5UdpTransport.cs`; which reads race disposal; whether the C4 PRD's four line numbers are correct/complete; disposal guards; the D7 fix shape; and what is unsafe to change.
- **Scope**: internal, read-only. Both files read in full (`rg -uu -p` + `sed`).
- **PRD claims under test**: unguarded post-dispose token reads at `Socks5UdpTransport.cs:255,282,294` and `Socks5ControlConnection.cs:295`.

## Headline correction

**The PRD's four line numbers do not point at token reads that race disposal.**

| PRD line | Actual content | Token read? | Races disposal? |
|----------|----------------|:-----------:|:---------------:|
| `Socks5UdpTransport.cs:255` | `_ = _socket.SendTo(_sendBuffer.AsSpan(0, written), SocketFlags.None, _relaySocketAddress);` | **no** (sync `SendTo`, returns `int`) | no |
| `Socks5UdpTransport.cs:282` | `_ = await _socket.SendToAsync(..., cancellationToken).ConfigureAwait(false);` (`SendAfterGateAsync`) | method parameter, awaited | no |
| `Socks5UdpTransport.cs:294` | same shape in `SendOverlappedAsync` | method parameter, awaited | no |
| `Socks5ControlConnection.cs:295` | `if (_attemptCancellation.IsCancellationRequested) throw new IOException(...)` | **`IsCancellationRequested`, not `.Token`** | **no** — `IsCancellationRequested` is safe after `Dispose` |

The *actual* disposal race in this pair is `Socks5ControlConnection`'s owned `_attemptCancellation`: reads of the `AttemptToken` property (`:208`, which evaluates `_attemptCancellation.Token`) from `RunWithinAttemptAsync` (`:265`, `:279`) can race `DisposeAsync`'s `_attemptCancellation.Dispose()` (`:244`). Those are the lines C4's PRD should name.

Verified by source:
- `Socks5ControlConnection.cs:230-247` `DisposeAsync`; `:244` `_attemptCancellation.Dispose();`.
- `Socks5ControlConnection.cs:263-271` and `:277-286` two `RunWithinAttemptAsync` overloads; each begins `using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(AttemptToken, cancellationToken);` at `:265` / `:279`.
- `Socks5ControlConnection.cs:208` `private CancellationToken AttemptToken => _attemptCancellation.Token;`.

---

## `Socks5ControlConnection.cs` — full CTS inventory

Physical lines 399; effective 304 (file 1).

### CTS creation

| `file:line` | Creation | Owner | Lifetime |
|-------------|----------|-------|----------|
| `:171` | `attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);` in `ConnectOnceAsync` | becomes the connection's `_attemptCancellation` (ctor `:42`) | per connect attempt |
| `:172` | `attemptCancellation.CancelAfter(timeout);` | same | per-attempt timeout (L1) |
| `:265` | `operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(AttemptToken, cancellationToken);` | local `using` in `RunWithinAttemptAsync` (udp-associate overload) | one operation |
| `:279` | same, connect-destination overload | local `using` | one operation |
| `:365` | `resolutionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);` | local `using` in address resolution | one resolution |

The field is stored at `:42` (`_attemptCancellation = attemptCancellation;`) and declared at `src/WinForward.Runtime/Socks5/Socks5ControlConnection.cs:32`.

### CTS disposal

| `file:line` | Disposal | Context |
|-------------|----------|---------|
| `:244` | `_attemptCancellation.Dispose();` | `DisposeAsync` `:230-247` (inside nested `finally`s after `_stream.DisposeAsync()` `:234` and `_loopPrevention?.Dispose()` `:240`) |
| `:397` | `attemptCancellation?.Dispose();` | `DisposeFailedAttemptAsync` `:387-398` — only when the connection was never constructed (local `attemptCancellation` nulled at `:181` on success) |
| `:265`/`:279`/`:365` | implicit | `using var` disposes each local operation/resolution CTS |

### Token reads

| `file:line` | Read | Safe after dispose? |
|-------------|------|:-------------------:|
| `:173` | `attemptCancellation.Token` for `socket.ConnectAsync` (before transfer; not yet a field) | n/a |
| `:182` | `connection.AttemptToken` for `AuthenticateAsync` (live field) | within connect, before publish |
| `:208` | `_attemptCancellation.Token` inside the `AttemptToken` property | **thrower if disposed** |
| `:265` | `CreateLinkedTokenSource(AttemptToken, cancellationToken)` | **thrower if disposed** (race vs `:244`) |
| `:268` | `operation(operationCancellation.Token)` | local, safe |
| `:279` | `CreateLinkedTokenSource(AttemptToken, cancellationToken)` | **thrower if disposed** (race vs `:244`) |
| `:282` | `operation(operationCancellation.Token)` | local, safe |
| `:295` | `_attemptCancellation.IsCancellationRequested` | **safe** (`IsCancellationRequested` does not throw post-`Dispose`) |
| `:365` | `resolutionCancellation.Token` / caller `cancellationToken` `:367`,`:372` | local/caller, safe |

### Disposal guard

**None.** No `_disposed`/`Interlocked`/single-flight field anywhere in the type (verified by `rg`). `DisposeAsync` `:230-247` relies on `NetworkStream.DisposeAsync`, `_loopPrevention.Dispose`, and `CancellationTokenSource.Dispose` being individually idempotent.

### Race window (the real one)

`UdpAssociateAsync` (`:212-220`) and `ConnectDestinationAsync` (`:222-228`) call `RunWithinAttemptAsync` (`:263`, `:277`), which read `AttemptToken` at `:265`/`:279`. `RunWithinAttemptAsync` is an async operation that can be in flight while the owner disposes the connection:
- `Socks5UdpTransport.DisposeAsync` disposes `_control` at `:391`;
- `TcpProxyRelay.DisposeAsync` disposes `_control` at `TcpProxyRelay.cs:352`.
If a relay send or a destination connect is concurrently entering (or re-entering) `RunWithinAttemptAsync`, `_attemptCancellation.Token` (`:208`) is read after `_attemptCancellation.Dispose()` (`:244`) → `ObjectDisposedException` escapes from a path that callers do not expect to throw it.

### D7 fix shape (program's answer: the scope owns the CTS)

Under `async-lifetime.md` D7 only `QuiescenceScope` owns a `CancellationTokenSource`. For this type that means:
- the control connection should not own/dispose a CTS; it should read the **owner scope's** `Token` (`TcpProxyRelay`/`UdpProxySession` scope per C3), so the token's lifetime is the scope's and it stays valid until `DrainAsync` completes;
- the `:265`/`:279` linked-operation CTSes are per-operation locals whose lifetime is already bounded by their `using` — they read `AttemptToken` only to combine the scope token with the caller token; once the scope token replaces `AttemptToken`, the `.Token` access is on a live scope token, not a disposed CTS.
- **Open wrinkle (not resolved by the repository)**: `_attemptCancellation` also carries the per-attempt timeout (`CancelAfter(timeout)` `:172`, checked at `:295`). A scope-owned CTS cannot hold a per-attempt `CancelAfter` without timing out the whole scope. How the attempt timeout is expressed once the CTS moves to the scope is a design decision C4's `design.md` must make; the working tree does not contain a precedent for it (C3 scopes have no `CancelAfter`).

### Not safe to change

- The `_attemptCancellation` per-attempt timeout semantics (L1 fail-closed, 'next candidate' retry) are pinned by `Socks5ControlTimeoutTests` (see `existing-tests.md`) — e.g. `ServerAddressResolutionTimeoutDoesNotDependOnResolverCancellation`, `HandshakeTimeoutIncludesMethodSelectionRead`, `CommandTimeoutIncludesReplyRead`.
- `_handshakeScratch` (`:35`) is a reused buffer for request/reply encoding; do not add allocation around it.
- `DisposeFailedAttemptAsync` (`:387-398`) disposing the *local* `attemptCancellation` on the failure path is correct and must not be replaced by a field read (the local is nulled at `:181` on success).

---

## `Socks5UdpTransport.cs` — full token inventory

Physical lines 402; effective 255 (file 1).

### CTS creation / disposal

**Zero.** The file contains no `CancellationTokenSource` (verified by `rg 'CancellationTokenSource'` → no hits). It stores no token field (fields `:125-131`, file 1). Every token it uses is a method parameter.

### Token reads

| `file:line` | Read | Origin | Races disposal? |
|-------------|------|--------|:---------------:|
| `:236` | `_sendGate.WaitAsync(cancellationToken)` (`SendSpanAsync`, warm path) | caller parameter | no (CTS unrelated) |
| `:243` | `payload.ToArray(), cancellationToken` → `SendAfterGateAsync` | parameter | no |
| `:259` | `SendOverlappedAsync(written, cancellationToken)` | parameter | no |
| `:282` | `_socket.SendToAsync(..., cancellationToken)` (`SendAfterGateAsync`) | parameter | no |
| `:294` | `_socket.SendToAsync(..., cancellationToken)` (`SendOverlappedAsync`) | parameter | no |
| `:317` | `_socket.ReceiveFromAsync(..., cancellationToken)` (`ReceiveAsync`) | parameter | no |

PRD's `:255`/`:282`/`:294` are a mix of a synchronous `int` discard and two awaited sends; **none reads an owned CTS token**, so none is a post-dispose token race. The PRD's line list for this file appears to be the `_ =`-discard allowlist sites from the C2 search (see `allowlist-shrink.md`), not CTS reads.

Where the parameters come from:
- receive token = `UdpProxySession._scope.Token` (`src/WinForward.Runtime/UdpProxy/UdpProxySession.cs:239`, passed to `_transport.ReceiveAsync` `:259`);
- send token = caller parameter threaded from `UdpProxyCoordinator.Send.cs:88` / `UdpSessionSetup.cs:165` via `UdpProxySession.cs:150`.

### Disposal guard

**None** — no `_disposed`/`Interlocked` latch. `DisposeAsync` `:375-401` is ordered: `_socket.Dispose()` `:379`; `finally _selfTrafficToken?.Dispose()` `:385`; `finally await _control.DisposeAsync()` `:391`; `finally _sendGate.Dispose()` `:397`.

### Genuine (non-CTS) disposal races in this type

These are the real hazards in the file, and they are **not** token reads:
- `_sendGate.Dispose()` `:397` races a sender that has not yet entered `_sendGate.WaitAsync(cancellationToken)` `:236` → `ObjectDisposedException` from `SendSpanAsync`. The comment `:395-396` assumes senders are already parked inside the gate and release it from their `finally`; a sender between the dispatcher call and `WaitAsync` is not covered.
- `_socket.Dispose()` `:379` races in-flight `SendToAsync`/`ReceiveFromAsync`; the reliable-UDP protocol treats socket disposal faults as the normal stop signal (`UdpProxySession.DisposeCoreAsync` catches `ObjectDisposedException` `UdpProxySession.cs:226-230`, comment `:227-229`).
- `_control.DisposeAsync()` `:391` is the same `_attemptCancellation` race as above (control owns the CTS).

### D7 fix shape

The transport owns no CTS, so D7 does not add one here. The transport should keep receiving the owner scope's `Token` (it already does via `UdpProxySession._scope.Token`); the D7 work is entirely inside `Socks5ControlConnection` and its owners. A guard, if added, must be a plain `Interlocked` `int` flag (allocation-free) — not a CTS, not a lock, not a lease per datagram.

### Not safe to change

- `SendSpanAsync` `:230-269` is a **hot-path warm entry**: non-async by design (`#pragma warning disable RCS1229` `:229`, `hot-path.md` #3), zero-alloc when the gate is uncontended, with the contended branch's `payload.ToArray()` `:243` the single documented cold-path allocation. Adding a closure, an `async` hop, a guard object allocation, or a per-datagram CTS here breaks `HotPathAllocationGateTests.EstablishedUdpDatagramPathAllocatesNoManagedBytes` and `Socks5UdpTransportSendTests.WarmSyncSendAllocatesNoManagedBytes` / `SendSpanAsyncWarmPathRunsNoAsyncStateMachine`.
- The `_sendGate` "dispose last" ordering (`:395-397`) and the sync-`SendTo` warm shape (`:255`) are the zero-alloc contract; C4 must not restructure them.
- `_sendBuffer` reuse (`:129`) and `_receiveSenderTemplate` (`:131`) are allocation-free by design.
