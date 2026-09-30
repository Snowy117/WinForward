# Design — the fake server's ASSOCIATE-reply counter race

The failure is a fixture-ordering race, not a pool defect. The design is correspondingly small; its value is
in the deterministic proof and in not re-litigating the refuted candidates.

## 1. The race, exactly

```
ScriptedSocks5UdpServer.cs:185-186        await stream.WriteAsync(reply, token);
                                          Interlocked.Increment(ref _associateReplies);
Socks5ControlConnection.UdpAssociateAsync (the client)
                                          ... reads that reply, completes the rent
```

The client's rent completes when it **reads** the reply; the fake records the reply **after** writing it. So
`AssociateReplyCount` is a progress report that can legitimately lag the client's completion by a scheduling
window — and under suite thread-pool load that window opens. The 630 ms test rents 4,500 flows, so the
assertion at `UdpAssociationHeadTests.cs:110` runs within microseconds of the last client completion, which
is exactly where the window bites.

Refuted candidates (with the lines that refute them) are listed in `prd.md`; they are not re-tested.

## 2. Fix: publish the counter before the reply is readable

Move the increment **before** `await stream.WriteAsync(reply, token)`. Then the counter is always ≥ the
readable state, so every dependent assertion observes either the reply already read (counter already
incremented) or a reply not yet readable (counter may be one ahead, which none of the assertions forbids —
all ten read it *after* awaiting their rents, and none asserts "exactly the number of replies I have read"
at a moment where a write is in flight).

Rejected alternative: a bounded post-state wait in the ten assertions. It multiplies a retry across five
files to paper over one wrong ordering, and it is the shape R4 of the predecessor tasks already treats as a
weakening.

`UdpAssociationHeadTests.cs:104-111` is **not touched**: the pool's shape guard is the reason the test
exists, and the artifact proves it was passing.

## 3. The deterministic proof (the part that makes this a fix and not a hope)

A statistical loop cannot prove an ordering fix. The fake gets a **test-only seam** — a hook that runs after
the counter is published, the reply bytes are prepared, and before (or instead of) the write — and a test that:

1. arms the seam to block the reply write on a `TaskCompletionSource`,
2. starts a rent on a background task,
3. waits on the **gate's own entered signal** — not `ConnectionCount`, which is incremented at accept
   (`ScriptedSocks5UdpServer.cs:136`) before the greeting and the ASSOCIATE exchange and therefore cannot
   establish that the server has reached the reply write,
4. asserts `server.AssociateReplyCount == 1` **while the write is still blocked**,
5. releases the write and awaits the rent to confirm the normal path still completes.

With the old ordering the counter is 0 at step 4 → the test fails before the fix; with the new ordering it is
1 → the test passes. The seam lives in the test fixture only, so the product is untouched.

## 4. The statistical half

The full suite is the flake's habitat (no isolated run of this test exists in any record). The acceptance
loop runs the **full suite** ≥20×, keeping each run's raw output, with the rate and its CI reported
(≥20 runs is ~80 % power against the observed 1/13 ≈ 7.7 %); the class-filter loop is the cheap check that
the test itself stays green and that the filter matches the class's measured `Total: 10` (a bare `Total: 1` guard would
also match the Analyzers summary line — vacuous).

## 5. Spec and record corrections

- `.trellis/spec/backend/udp-relay.md` (§522, tests §595-613) gains two invariants: the pool publishes the
  association into its set and claims the lease **before** the ASSOCIATE await; and a fixture that reports
  protocol progress must publish its counter **before** the bytes are readable, or the tests that read it
  are racy by construction. The ten dependent assertions are named.
- The task record corrects the archive record's and the journal's misattribution of the failure to
  `pool.AssociationCount`; the correction is part of the deliverable, because two downstream records
  currently point at the wrong invariant.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Moving the increment changes another test's expectation | the ten dependent assertions are enumerated; each reads the counter after awaiting its rents, and the class filter plus the ≥20 suite runs cover them |
| The seam's blocking write deadlocks the fake's read loop | the seam blocks only the reply write, which the client is waiting for; the test releases it on a path that always runs (try/finally) |
| The full-suite loop cannot reproduce in 20 runs | report the CI and keep the deterministic test as the proof of the fix; a still-failing suite is new evidence, not a reason to widen the assertion |
