```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.51GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method         | Cardinality | Mean     | Error     | StdDev   | Allocated |
|--------------- |------------ |---------:|----------:|---------:|----------:|
| **ResolveMissing** | **0**           | **43.33 ns** |  **2.357 ns** | **0.129 ns** |         **-** |
| **ResolveMissing** | **1000**        | **94.11 ns** | **24.824 ns** | **1.361 ns** |         **-** |
| **ResolveMissing** | **16384**       | **93.75 ns** | **27.796 ns** | **1.524 ns** |         **-** |
| **ResolveMissing** | **65535**       | **98.73 ns** |  **7.121 ns** | **0.390 ns** |         **-** |
