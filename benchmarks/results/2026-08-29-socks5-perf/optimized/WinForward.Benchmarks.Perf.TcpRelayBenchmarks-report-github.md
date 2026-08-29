```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.51GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method      | ChunkBytes | Mean      | Error      | StdDev   |
|------------ |----------- |----------:|-----------:|---------:|
| **OneWayAsync** | **1**          | **172.67 ms** | **163.491 ms** | **8.961 ms** |
| **OneWayAsync** | **1024**       |  **20.30 ms** |  **12.515 ms** | **0.686 ms** |
| **OneWayAsync** | **8192**       |  **18.37 ms** |   **7.716 ms** | **0.423 ms** |
| **OneWayAsync** | **65536**      |  **18.94 ms** |   **6.749 ms** | **0.370 ms** |
