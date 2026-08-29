```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.51GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method       | Cardinality | Mean      | Error     | StdDev   | Allocated |
|------------- |------------ |----------:|----------:|---------:|----------:|
| **WildcardMiss** | **0**           |  **93.05 ns** |  **4.619 ns** | **0.253 ns** |         **-** |
| **WildcardMiss** | **1000**        | **254.35 ns** | **16.637 ns** | **0.912 ns** |         **-** |
| **WildcardMiss** | **16384**       | **255.42 ns** | **28.941 ns** | **1.586 ns** |         **-** |
| **WildcardMiss** | **65535**       | **249.29 ns** | **19.126 ns** | **1.048 ns** |         **-** |
