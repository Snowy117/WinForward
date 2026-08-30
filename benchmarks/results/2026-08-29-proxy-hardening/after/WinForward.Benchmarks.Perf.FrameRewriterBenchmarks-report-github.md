```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.47GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                             | FrameSize | Mean      | Error     | StdDev    | Allocated |
|----------------------------------- |---------- |----------:|----------:|----------:|----------:|
| **ClassifyTcpSyn**                     | **128**       | **49.237 ns** | **14.719 ns** | **0.8068 ns** |         **-** |
| SwapEthernetMacs                   | 128       |  4.368 ns |  1.011 ns | 0.0554 ns |         - |
| TryRewriteForwardLegHostShape      | 128       | 29.526 ns |  5.650 ns | 0.3097 ns |         - |
| TryRewriteForwardLegForwardedShape | 128       | 33.368 ns | 47.781 ns | 2.6190 ns |         - |
| TryRewriteEndpointsDirect          | 128       | 36.960 ns | 75.194 ns | 4.1216 ns |         - |
| **ClassifyTcpSyn**                     | **1400**      | **42.966 ns** |  **3.692 ns** | **0.2024 ns** |         **-** |
| SwapEthernetMacs                   | 1400      |  4.781 ns |  8.269 ns | 0.4532 ns |         - |
| TryRewriteForwardLegHostShape      | 1400      | 49.653 ns |  5.495 ns | 0.3012 ns |         - |
| TryRewriteForwardLegForwardedShape | 1400      | 34.835 ns | 11.750 ns | 0.6441 ns |         - |
| TryRewriteEndpointsDirect          | 1400      | 33.386 ns | 13.843 ns | 0.7588 ns |         - |
