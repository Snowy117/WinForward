```

BenchmarkDotNet v0.15.8, Linux NixOS 26.11 (Zokor)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                    | Mean      | Error     | StdDev   | Allocated |
|-------------------------- |----------:|----------:|---------:|----------:|
| ResolveSameOrientationHit | 117.29 ns | 24.989 ns | 1.370 ns |         - |
| ResolveReverseAliasHit    | 138.42 ns |  9.812 ns | 0.538 ns |         - |
| ReadActivityClock         |  40.56 ns |  3.448 ns | 0.189 ns |         - |
