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
| WarmPassDisabledTraceAsync    | 142.0 ns |  15.32 ns |  0.84 ns | 0.0095 |     160 B |
| WarmProxyDisabledTraceAsync   | 128.9 ns |  38.88 ns |  2.13 ns | 0.0095 |     160 B |
| WarmPassProductionAsync       | 152.5 ns |  76.93 ns |  4.22 ns | 0.0095 |     160 B |
| WarmProxyProductionAsync      | 161.0 ns |  44.52 ns |  2.44 ns | 0.0095 |     160 B |
| ReverseCandidateSlowPathAsync | 346.6 ns | 266.39 ns | 14.60 ns | 0.0157 |     264 B |
