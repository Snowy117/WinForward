```

BenchmarkDotNet v0.15.8, Linux NixOS 26.11 (Zokor)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method    | Workers | Mean     | Error     | StdDev  | Allocated |
|---------- |-------- |---------:|----------:|--------:|----------:|
| **ReadySend** | **1**       | **361.4 ns** |  **64.07 ns** | **3.51 ns** |         **-** |
| **ReadySend** | **2**       | **403.6 ns** |  **93.69 ns** | **5.14 ns** |         **-** |
| **ReadySend** | **4**       | **523.3 ns** | **145.31 ns** | **7.97 ns** |         **-** |
