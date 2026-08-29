```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.51GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method        | FrameBytes | BatchCapacity | Mean     | Error     | StdDev   | Gen0     | Gen1     | Gen2     | Allocated |
|-------------- |----------- |-------------- |---------:|----------:|---------:|---------:|---------:|---------:|----------:|
| **EndToEndAsync** | **128**        | **1**             | **67.65 ms** |  **2.006 ms** | **0.110 ms** | **375.0000** | **375.0000** | **375.0000** |   **1.86 MB** |
| **EndToEndAsync** | **128**        | **32**            | **65.12 ms** | **25.176 ms** | **1.380 ms** | **375.0000** | **375.0000** | **375.0000** |   **1.86 MB** |
| **EndToEndAsync** | **1400**       | **1**             | **68.88 ms** |  **9.375 ms** | **0.514 ms** | **375.0000** | **375.0000** | **375.0000** |   **1.86 MB** |
| **EndToEndAsync** | **1400**       | **32**            | **69.64 ms** |  **2.237 ms** | **0.123 ms** | **285.7143** | **285.7143** | **285.7143** |   **1.86 MB** |
