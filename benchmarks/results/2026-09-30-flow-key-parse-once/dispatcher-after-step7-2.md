```

BenchmarkDotNet v0.15.8, Linux NixOS 26.11 (Zokor)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                        | Mean     | Error     | StdDev   | Gen0   | Allocated |
|------------------------------ |---------:|----------:|---------:|-------:|----------:|
| WarmPassDisabledTraceAsync    | 149.9 ns |  23.02 ns |  1.26 ns | 0.0095 |     160 B |
| WarmProxyDisabledTraceAsync   | 129.1 ns |  44.22 ns |  2.42 ns | 0.0095 |     160 B |
| WarmPassProductionAsync       | 150.2 ns |  42.22 ns |  2.31 ns | 0.0095 |     160 B |
| WarmProxyProductionAsync      | 155.9 ns |  27.66 ns |  1.52 ns | 0.0095 |     160 B |
| ReverseCandidateSlowPathAsync | 318.7 ns | 226.43 ns | 12.41 ns | 0.0157 |     264 B |
