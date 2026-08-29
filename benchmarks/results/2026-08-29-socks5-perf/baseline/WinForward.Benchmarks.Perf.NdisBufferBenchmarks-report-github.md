```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method             | FrameBytes | Mean       | Error      | StdDev    | Gen0   | Allocated |
|------------------- |----------- |-----------:|-----------:|----------:|-------:|----------:|
| **AllocateSetDispose** | **64**         |  **91.501 ns** | **23.9188 ns** | **1.3111 ns** | **0.0024** |      **40 B** |
| ReuseSet           | 64         |   6.831 ns |  1.5246 ns | 0.0836 ns |      - |         - |
| **AllocateSetDispose** | **512**        |  **94.947 ns** | **11.1449 ns** | **0.6109 ns** | **0.0024** |      **40 B** |
| ReuseSet           | 512        |  11.710 ns |  0.6702 ns | 0.0367 ns |      - |         - |
| **AllocateSetDispose** | **1514**       | **103.908 ns** | **42.4000 ns** | **2.3241 ns** | **0.0024** |      **40 B** |
| ReuseSet           | 1514       |  29.941 ns |  1.1478 ns | 0.0629 ns |      - |         - |
