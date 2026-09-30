```

BenchmarkDotNet v0.15.8, Linux NixOS 26.11 (Zokor)
AMD Ryzen 9 9955HX 2.49GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                        | Mean     | Error    | StdDev  | Gen0   | Allocated |
|------------------------------ |---------:|---------:|--------:|-------:|----------:|
| WarmPassDisabledTraceAsync    | 137.3 ns | 37.94 ns | 2.08 ns | 0.0095 |     160 B |
| WarmProxyDisabledTraceAsync   | 129.7 ns | 57.33 ns | 3.14 ns | 0.0095 |     160 B |
| WarmPassProductionAsync       | 151.4 ns | 53.44 ns | 2.93 ns | 0.0095 |     160 B |
| WarmProxyProductionAsync      | 160.3 ns | 90.15 ns | 4.94 ns | 0.0095 |     160 B |
| ReverseCandidateSlowPathAsync | 331.4 ns | 95.20 ns | 5.22 ns | 0.0157 |     264 B |
