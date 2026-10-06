```

BenchmarkDotNet v0.15.8, Linux NixOS 26.11 (Zokor)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]         : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  .NET 10.0      : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  NativeAOT 10.0 : .NET 10.0.12, X64 NativeAOT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method         | Job            | Runtime        | FrameBytes | Mean       | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------- |--------------- |--------------- |----------- |-----------:|----------:|----------:|------:|--------:|----------:|------------:|
| **ScalarFallback** | **.NET 10.0**      | **.NET 10.0**      | **20**         |  **10.259 ns** | **0.1258 ns** | **0.1883 ns** |  **1.00** |    **0.03** |         **-** |          **NA** |
| Production     | .NET 10.0      | .NET 10.0      | 20         |  11.329 ns | 0.0950 ns | 0.1393 ns |  1.10 |    0.02 |         - |          NA |
| ScalarFallback | NativeAOT 10.0 | NativeAOT 10.0 | 20         |  10.698 ns | 0.1042 ns | 0.1559 ns |  1.04 |    0.02 |         - |          NA |
| Production     | NativeAOT 10.0 | NativeAOT 10.0 | 20         |  13.192 ns | 0.1579 ns | 0.2314 ns |  1.29 |    0.03 |         - |          NA |
|                |                |                |            |            |           |           |       |         |           |             |
| **ScalarFallback** | **.NET 10.0**      | **.NET 10.0**      | **64**         |  **32.303 ns** | **0.2228 ns** | **0.3334 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Production     | .NET 10.0      | .NET 10.0      | 64         |   4.639 ns | 0.0534 ns | 0.0783 ns |  0.14 |    0.00 |         - |          NA |
| ScalarFallback | NativeAOT 10.0 | NativeAOT 10.0 | 64         |  32.454 ns | 0.1605 ns | 0.2143 ns |  1.00 |    0.01 |         - |          NA |
| Production     | NativeAOT 10.0 | NativeAOT 10.0 | 64         |   6.480 ns | 0.0978 ns | 0.1433 ns |  0.20 |    0.00 |         - |          NA |
|                |                |                |            |            |           |           |       |         |           |             |
| **ScalarFallback** | **.NET 10.0**      | **.NET 10.0**      | **512**        | **272.612 ns** | **1.5129 ns** | **2.2644 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Production     | .NET 10.0      | .NET 10.0      | 512        |  11.081 ns | 0.1197 ns | 0.1791 ns |  0.04 |    0.00 |         - |          NA |
| ScalarFallback | NativeAOT 10.0 | NativeAOT 10.0 | 512        | 273.022 ns | 1.7224 ns | 2.3576 ns |  1.00 |    0.01 |         - |          NA |
| Production     | NativeAOT 10.0 | NativeAOT 10.0 | 512        |  13.069 ns | 0.2120 ns | 0.2971 ns |  0.05 |    0.00 |         - |          NA |
|                |                |                |            |            |           |           |       |         |           |             |
| **ScalarFallback** | **.NET 10.0**      | **.NET 10.0**      | **1514**       | **781.622 ns** | **4.3027 ns** | **6.1707 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Production     | .NET 10.0      | .NET 10.0      | 1514       |  45.521 ns | 0.4032 ns | 0.5652 ns |  0.06 |    0.00 |         - |          NA |
| ScalarFallback | NativeAOT 10.0 | NativeAOT 10.0 | 1514       | 776.845 ns | 3.1913 ns | 4.4737 ns |  0.99 |    0.01 |         - |          NA |
| Production     | NativeAOT 10.0 | NativeAOT 10.0 | 1514       |  49.418 ns | 0.3711 ns | 0.5439 ns |  0.06 |    0.00 |         - |          NA |
