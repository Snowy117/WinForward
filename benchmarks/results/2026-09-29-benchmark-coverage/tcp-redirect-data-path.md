```

BenchmarkDotNet v0.15.8, Linux NixOS 26.11 (Zokor)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method              | Ipv6  | FrameSize | Mean      | Error     | StdDev   | Allocated |
|-------------------- |------ |---------- |----------:|----------:|---------:|----------:|
| **ForwardLegHost**      | **False** | **128**       | **116.01 ns** | **23.222 ns** | **1.273 ns** |         **-** |
| ForwardLegForwarded | False | 128       |  92.43 ns | 31.626 ns | 1.734 ns |         - |
| ReverseLegHost      | False | 128       |  62.24 ns |  8.801 ns | 0.482 ns |         - |
| ReverseLegForwarded | False | 128       |  51.12 ns |  4.741 ns | 0.260 ns |         - |
| **ForwardLegHost**      | **False** | **1400**      | **118.60 ns** | **32.354 ns** | **1.773 ns** |         **-** |
| ForwardLegForwarded | False | 1400      | 102.90 ns | 24.349 ns | 1.335 ns |         - |
| ReverseLegHost      | False | 1400      |  68.63 ns | 16.618 ns | 0.911 ns |         - |
| ReverseLegForwarded | False | 1400      |  58.03 ns | 21.937 ns | 1.202 ns |         - |
| **ForwardLegHost**      | **True**  | **128**       | **106.21 ns** | **42.595 ns** | **2.335 ns** |         **-** |
| ForwardLegForwarded | True  | 128       |  87.48 ns | 30.360 ns | 1.664 ns |         - |
| ReverseLegHost      | True  | 128       |  87.69 ns | 44.950 ns | 2.464 ns |         - |
| ReverseLegForwarded | True  | 128       | 103.50 ns | 12.304 ns | 0.674 ns |         - |
| **ForwardLegHost**      | **True**  | **1400**      | **111.20 ns** | **59.205 ns** | **3.245 ns** |         **-** |
| ForwardLegForwarded | True  | 1400      |  91.52 ns | 14.817 ns | 0.812 ns |         - |
| ReverseLegHost      | True  | 1400      |  96.22 ns |  3.024 ns | 0.166 ns |         - |
| ReverseLegForwarded | True  | 1400      | 112.02 ns |  9.848 ns | 0.540 ns |         - |
