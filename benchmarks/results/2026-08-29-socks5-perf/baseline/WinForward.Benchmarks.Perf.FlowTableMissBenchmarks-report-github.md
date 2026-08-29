```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method         | Cardinality | Mean      | Error    | StdDev   | Allocated |
|--------------- |------------ |----------:|---------:|---------:|----------:|
| **ResolveMissing** | **0**           |  **41.81 ns** | **2.877 ns** | **0.158 ns** |         **-** |
| **ResolveMissing** | **1000**        |  **95.51 ns** | **0.884 ns** | **0.048 ns** |         **-** |
| **ResolveMissing** | **16384**       | **106.59 ns** | **4.257 ns** | **0.233 ns** |         **-** |
| **ResolveMissing** | **65535**       |  **86.30 ns** | **0.490 ns** | **0.027 ns** |         **-** |
