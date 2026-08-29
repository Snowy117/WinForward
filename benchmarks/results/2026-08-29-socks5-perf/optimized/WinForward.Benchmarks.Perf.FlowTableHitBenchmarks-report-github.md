```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.51GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                 | Cardinality | Mean     | Error    | StdDev  | Allocated |
|----------------------- |------------ |---------:|---------:|--------:|----------:|
| **ResolveCrossAdapterHit** | **1000**        | **169.7 ns** | **12.25 ns** | **0.67 ns** |         **-** |
| **ResolveCrossAdapterHit** | **16384**       | **174.6 ns** | **88.55 ns** | **4.85 ns** |         **-** |
| **ResolveCrossAdapterHit** | **65535**       | **166.3 ns** | **27.17 ns** | **1.49 ns** |         **-** |
