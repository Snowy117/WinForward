# Diagnose the UDP association head count flake

## Goal

Part 3 of the operator's 'fix all suspicious tests' item. Observed once in 13 proof runs of 09-30-exact-gate-residual-lumps: UdpAssociationHeadTests.TheDefaultHeadKeepsTheAcceptanceLoadShared failed with Expected: 282, Actual: 281 at UdpAssociationHeadTests.cs:104 (pool.AssociationCount). The test rents 4,500 flows at the default 16 flows per association and asserts the shared shape ceil(4500/16) = 282 associations, 282 control connections and 282 ASSOCIATE replies; 281 means the sharing invariant the UDP reuse PRD acceptance rests on was observed one association short. Not an allocation gate and not the residual host lump (that one is signature-matched at 168/5216/7384/7448 B and carried by 09-30-exact-gate-residual-lumps). Candidate mechanism to test first: a race between AssociationCount and the rent completion (an association counted only after its handshake completes while RentAsync already returned a lease from it), or a lease path that reuses a slot differently under load.

## Requirements

- TBD

## Acceptance Criteria

- [ ] TBD

## Notes

- Keep `prd.md` focused on requirements, constraints, and acceptance criteria.
- Lightweight tasks can remain PRD-only.
- For complex tasks, add `design.md` for technical design and `implement.md` for execution planning before `task.py start`.
