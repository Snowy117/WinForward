# B9 attempt 2 — memory environment

- Attempt 2 preflight (2026-10-19 01:01:41 VM) recorded totalRamMb 1831, free 521,
  against the calibration/attempt 1 value of 6114 MB. The VM's dynamic memory booted at its
  low starting point after the Windows Update restart.
- At 01:06 VM a native 4 GB allocation (VirtualAlloc + RtlFillMemory, pid 6928) raised
  TotalVisibleMemorySize to 6,423,488 KB (6272 MB), then 7,228,352 KB (7059 MB). Stopping it at
  01:18 dropped the total to 3,421,120 KB (3341 MB); the host reclaims the dynamic memory.
- At 01:18 VM a native 3 GB allocation (pid 2944, 30,600 s) brought TotalVisibleMemorySize to
  6,071,104 KB (5929 MB), free 4,941,664 KB. Calibration 6114 MB; 3.0% difference; inside the
  10% calibration band.
- The 3 GB allocation oscillated as the host reclaimed memory (5929 -> 4461 MB). At 01:56 a
  2 GB allocation (pid 3672, 30,600 s) was added; at the pass 2 start (02:15 VM) the total was
  6,519,616 KB (6367 MB), free 1.2 GB, +4.1% against the calibration 6114 MB.
- The 3 GB + 2 GB allocations are held through the measured passes. Pass 1 is the discarded
  warm-up and is not memory evidence; its 4.5-7.0 GB oscillation is excluded by the discard rule.
- The report must state: preflight 1831 MB; warm-up oscillated 3.3-7.0 GB; measured passes 2-7
  started at 6367 MB and are checked at pass boundaries; calibration 6114 MB. The +4.1% is inside
  the 10% band, but the 5 GB artificial occupancy is a session-specific limit. If a measured pass
  boundary falls below 5500 MB (the 10% band), the memory family must be marked not comparable.
