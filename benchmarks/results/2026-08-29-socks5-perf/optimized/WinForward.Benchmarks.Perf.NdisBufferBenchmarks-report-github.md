```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.51GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method             | FrameBytes | Mean       | Error       | StdDev     | Gen0   | Allocated |
|------------------- |----------- |-----------:|------------:|-----------:|-------:|----------:|
| **AllocateSetDispose** | **64**         | **113.804 ns** |  **26.6132 ns** |  **1.4588 ns** | **0.0024** |      **40 B** |
| ReuseSet           | 64         |   8.001 ns |   0.8014 ns |  0.0439 ns |      - |         - |
| **AllocateSetDispose** | **512**        | **105.903 ns** |  **29.2541 ns** |  **1.6035 ns** | **0.0024** |      **40 B** |
| ReuseSet           | 512        |  13.740 ns |   3.5263 ns |  0.1933 ns |      - |         - |
| **AllocateSetDispose** | **1514**       | **117.345 ns** | **275.1200 ns** | **15.0803 ns** | **0.0024** |      **40 B** |
| ReuseSet           | 1514       |  27.375 ns |   5.8960 ns |  0.3232 ns |      - |         - |
