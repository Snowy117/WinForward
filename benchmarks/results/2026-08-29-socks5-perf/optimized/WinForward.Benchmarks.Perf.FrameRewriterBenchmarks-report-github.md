```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.51GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                             | FrameSize | Mean       | Error      | StdDev    | Allocated |
|----------------------------------- |---------- |-----------:|-----------:|----------:|----------:|
| **ClassifyTcpSyn**                     | **128**       |  **39.605 ns** |   **6.162 ns** | **0.3377 ns** |         **-** |
| SwapEthernetMacs                   | 128       |   4.437 ns |   1.466 ns | 0.0803 ns |         - |
| TryRewriteForwardLegHostShape      | 128       |  86.843 ns | 160.162 ns | 8.7790 ns |         - |
| TryRewriteForwardLegForwardedShape | 128       |  73.503 ns |  25.069 ns | 1.3741 ns |         - |
| **ClassifyTcpSyn**                     | **1400**      |  **40.565 ns** |   **8.259 ns** | **0.4527 ns** |         **-** |
| SwapEthernetMacs                   | 1400      |   4.528 ns |   1.908 ns | 0.1046 ns |         - |
| TryRewriteForwardLegHostShape      | 1400      | 642.235 ns | 128.783 ns | 7.0590 ns |         - |
| TryRewriteForwardLegForwardedShape | 1400      | 631.196 ns |  51.956 ns | 2.8479 ns |         - |
