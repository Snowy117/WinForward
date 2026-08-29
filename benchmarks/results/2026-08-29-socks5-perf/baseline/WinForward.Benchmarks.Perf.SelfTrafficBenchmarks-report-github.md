```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method       | Cardinality | Mean      | Error     | StdDev   | Allocated |
|------------- |------------ |----------:|----------:|---------:|----------:|
| **WildcardMiss** | **0**           |  **97.75 ns** |  **6.705 ns** | **0.368 ns** |         **-** |
| **WildcardMiss** | **1000**        | **240.11 ns** |  **4.595 ns** | **0.252 ns** |         **-** |
| **WildcardMiss** | **16384**       | **250.96 ns** | **14.220 ns** | **0.779 ns** |         **-** |
| **WildcardMiss** | **65535**       | **252.22 ns** | **15.068 ns** | **0.826 ns** |         **-** |
