# Plots are not generated

The C# analysis writes `tables.md` and `verdict.json`, and nothing else: it renders no PNG and has
no plotting dependency. This file is written unconditionally, together with those two, so the
absence of `plots/` is stated rather than left to be inferred from an empty directory.

No number in either file comes from a plot. Every cell is computed from the campaign's own records:
the arm result records and sample series, `run.json`, the target ledgers and `environment.json`.

The seven plots the previous implementation drew — latency percentiles per arm, loss rates, the
reliability outcome mix, MIX private bytes, CPU per arm, the LAT p99 per pass and the dual-phase
direct-vs-proxied lanes — are not reproduced here. Their inputs are all still in `tables.md`, so the
plots can be rebuilt from it by any tool that reads markdown, without re-reading the raw tree.
