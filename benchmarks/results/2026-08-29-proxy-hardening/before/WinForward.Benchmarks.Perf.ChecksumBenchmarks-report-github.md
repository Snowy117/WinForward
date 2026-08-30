```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method          | FrameBytes | Mean       | Error      | StdDev     | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------- |----------- |-----------:|-----------:|-----------:|------:|--------:|----------:|------------:|
| **Scalar**          | **64**         |  **39.293 ns** |   **9.106 ns** |  **0.4991 ns** |  **1.00** |    **0.02** |         **-** |          **NA** |
| VectorCandidate | 64         |   3.825 ns |   4.160 ns |  0.2280 ns |  0.10 |    0.01 |         - |          NA |
|                 |            |            |            |            |       |         |           |             |
| **Scalar**          | **512**        | **354.442 ns** | **149.825 ns** |  **8.2124 ns** |  **1.00** |    **0.03** |         **-** |          **NA** |
| VectorCandidate | 512        |  17.808 ns |  65.013 ns |  3.5636 ns |  0.05 |    0.01 |         - |          NA |
|                 |            |            |            |            |       |         |           |             |
| **Scalar**          | **1514**       | **989.033 ns** | **541.308 ns** | **29.6709 ns** |  **1.00** |    **0.04** |         **-** |          **NA** |
| VectorCandidate | 1514       |  41.913 ns |  10.775 ns |  0.5906 ns |  0.04 |    0.00 |         - |          NA |
