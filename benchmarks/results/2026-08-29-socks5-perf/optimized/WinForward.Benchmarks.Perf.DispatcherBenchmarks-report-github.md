```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.51GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                      | Mean     | Error    | StdDev  | Gen0   | Allocated |
|---------------------------- |---------:|---------:|--------:|-------:|----------:|
| WarmPassDisabledTraceAsync  | 222.3 ns | 19.29 ns | 1.06 ns | 0.0095 |     160 B |
| WarmProxyDisabledTraceAsync | 287.7 ns | 19.37 ns | 1.06 ns | 0.0095 |     160 B |
