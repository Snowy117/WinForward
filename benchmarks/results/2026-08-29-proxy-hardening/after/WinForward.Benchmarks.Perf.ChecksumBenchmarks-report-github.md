```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method          | FrameBytes | Mean      | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------- |----------- |----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| **Scalar**          | **64**         |  **4.878 ns** |  **0.4244 ns** | **0.0233 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| VectorCandidate | 64         |  3.362 ns |  0.3464 ns | 0.0190 ns |  0.69 |    0.00 |         - |          NA |
|                 |            |           |            |           |       |         |           |             |
| **Scalar**          | **512**        | **20.349 ns** |  **2.4393 ns** | **0.1337 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| VectorCandidate | 512        | 15.009 ns |  9.7565 ns | 0.5348 ns |  0.74 |    0.02 |         - |          NA |
|                 |            |           |            |           |       |         |           |             |
| **Scalar**          | **1514**       | **61.736 ns** | **67.7193 ns** | **3.7119 ns** |  **1.00** |    **0.07** |         **-** |          **NA** |
| VectorCandidate | 1514       | 40.987 ns | 15.6041 ns | 0.8553 ns |  0.67 |    0.04 |         - |          NA |
