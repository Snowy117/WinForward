```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method      | ChunkBytes | Mean      | Error     | StdDev   | Allocated |
|------------ |----------- |----------:|----------:|---------:|----------:|
| **OneWayAsync** | **1**          | **168.89 ms** | **96.692 ms** | **5.300 ms** | **211.15 KB** |
| **OneWayAsync** | **1024**       |  **18.09 ms** |  **3.715 ms** | **0.204 ms** | **181.56 KB** |
| **OneWayAsync** | **8192**       |  **14.73 ms** |  **9.877 ms** | **0.541 ms** | **187.63 KB** |
| **OneWayAsync** | **65536**      |  **15.18 ms** |  **4.054 ms** | **0.222 ms** | **178.97 KB** |
