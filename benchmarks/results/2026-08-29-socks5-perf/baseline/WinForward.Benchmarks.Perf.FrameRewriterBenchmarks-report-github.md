```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                             | FrameSize | Mean         | Error       | StdDev     | Allocated |
|----------------------------------- |---------- |-------------:|------------:|-----------:|----------:|
| **ClassifyTcpSyn**                     | **128**       |    **39.304 ns** |   **1.3815 ns** |  **0.0757 ns** |         **-** |
| SwapEthernetMacs                   | 128       |     6.981 ns |   0.8483 ns |  0.0465 ns |         - |
| TryRewriteForwardLegHostShape      | 128       |    75.696 ns |  11.2763 ns |  0.6181 ns |         - |
| TryRewriteForwardLegForwardedShape | 128       |   250.145 ns |  63.9076 ns |  3.5030 ns |         - |
| **ClassifyTcpSyn**                     | **1400**      |    **40.907 ns** |  **10.7717 ns** |  **0.5904 ns** |         **-** |
| SwapEthernetMacs                   | 1400      |     7.145 ns |   2.4213 ns |  0.1327 ns |         - |
| TryRewriteForwardLegHostShape      | 1400      |   616.691 ns | 141.9821 ns |  7.7825 ns |         - |
| TryRewriteForwardLegForwardedShape | 1400      | 2,887.910 ns | 607.2793 ns | 33.2870 ns |         - |
