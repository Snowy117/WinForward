```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method      | ChunkBytes | Mean      | Error     | StdDev   |
|------------ |----------- |----------:|----------:|---------:|
| **OneWayAsync** | **1**          | **167.31 ms** | **19.288 ms** | **1.057 ms** |
| **OneWayAsync** | **1024**       |  **18.64 ms** |  **9.259 ms** | **0.508 ms** |
| **OneWayAsync** | **8192**       |  **16.72 ms** |  **4.539 ms** | **0.249 ms** |
| **OneWayAsync** | **65536**      |  **17.33 ms** | **15.184 ms** | **0.832 ms** |
