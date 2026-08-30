```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.47GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                             | FrameSize | Mean       | Error         | StdDev      | Allocated |
|----------------------------------- |---------- |-----------:|--------------:|------------:|----------:|
| **ClassifyTcpSyn**                     | **128**       |  **40.123 ns** |     **5.6936 ns** |   **0.3121 ns** |         **-** |
| SwapEthernetMacs                   | 128       |   4.304 ns |     0.7490 ns |   0.0411 ns |         - |
| TryRewriteForwardLegHostShape      | 128       |  81.579 ns |     4.2305 ns |   0.2319 ns |         - |
| TryRewriteForwardLegForwardedShape | 128       |  74.005 ns |    29.8114 ns |   1.6341 ns |         - |
| TryRewriteEndpointsDirect          | 128       |  73.212 ns |    23.2684 ns |   1.2754 ns |         - |
| **ClassifyTcpSyn**                     | **1400**      |  **40.397 ns** |    **11.1926 ns** |   **0.6135 ns** |         **-** |
| SwapEthernetMacs                   | 1400      |   4.567 ns |    12.2927 ns |   0.6738 ns |         - |
| TryRewriteForwardLegHostShape      | 1400      | 658.383 ns |   415.1182 ns |  22.7540 ns |         - |
| TryRewriteForwardLegForwardedShape | 1400      | 727.562 ns | 2,588.6196 ns | 141.8910 ns |         - |
| TryRewriteEndpointsDirect          | 1400      | 626.207 ns |   181.3494 ns |   9.9404 ns |         - |
