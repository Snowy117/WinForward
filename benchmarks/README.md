# WinForward performance harness

The harness is a dependency-free Release executable for repeatable managed-path measurements. It
does not exercise WinpkFilter, NDISAPI, Windows IP Helper, or ETW on any platform; each output
file records `managedOnly: true` in its metadata.

Run the full portable matrix:

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --output .trellis/tasks/08-17-performance-hotspots/research/baseline-managed.jsonl
```

Run a short validation pass:

```text
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --quick --no-relay
```

The JSONL schema records runtime/OS, workload parameters, elapsed and CPU time, operations/s,
allocated bytes/op, scenario byte volume (`copiedBytes`), working-set delta, and GC collection
counts. Parser cases use `copiedBytes` as bytes traversed, not an assertion that every byte was
materialized. Use allocation results for parser copy-removal comparisons. Use the same command,
machine, power settings, and runtime for before/after comparisons.

The matrix includes 1, 100, and 1,000 concurrently active UDP sessions. Their result is captured
before coordinator disposal so `workingSetDeltaBytes` includes the live receive-buffer pool rentals.
