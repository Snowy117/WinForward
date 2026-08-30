```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.48GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method      | ChunkBytes | Mean      | Error     | StdDev   | Gen0    | Allocated  |
|------------ |----------- |----------:|----------:|---------:|--------:|-----------:|
| **OneWayAsync** | **1**          | **179.52 ms** | **110.81 ms** | **6.074 ms** |       **-** | **1684.86 KB** |
| **OneWayAsync** | **1024**       |  **30.45 ms** |  **43.57 ms** | **2.388 ms** | **31.2500** |  **845.88 KB** |
| **OneWayAsync** | **8192**       |  **17.06 ms** |  **16.44 ms** | **0.901 ms** | **31.2500** |  **828.13 KB** |
| **OneWayAsync** | **65536**      |  **21.77 ms** |  **52.79 ms** | **2.893 ms** | **31.2500** |  **820.26 KB** |
