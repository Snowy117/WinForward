```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                 | Cardinality | Mean     | Error    | StdDev  | Allocated |
|----------------------- |------------ |---------:|---------:|--------:|----------:|
| **ResolveCrossAdapterHit** | **1000**        | **183.4 ns** | **52.95 ns** | **2.90 ns** |         **-** |
| **ResolveCrossAdapterHit** | **16384**       | **176.0 ns** |  **9.49 ns** | **0.52 ns** |         **-** |
| **ResolveCrossAdapterHit** | **65535**       | **170.2 ns** | **15.96 ns** | **0.88 ns** |         **-** |
