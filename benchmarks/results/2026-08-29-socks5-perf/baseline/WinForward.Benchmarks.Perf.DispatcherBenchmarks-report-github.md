```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                      | Mean     | Error     | StdDev  | Gen0   | Allocated |
|---------------------------- |---------:|----------:|--------:|-------:|----------:|
| WarmPassDisabledTraceAsync  | 226.4 ns |  46.12 ns | 2.53 ns | 0.0095 |     160 B |
| WarmProxyDisabledTraceAsync | 596.4 ns | 126.09 ns | 6.91 ns | 0.0210 |     352 B |
