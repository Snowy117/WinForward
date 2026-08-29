```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method        | FrameBytes | BatchCapacity | Mean     | Error     | StdDev   | Gen0     | Gen1     | Gen2     | Allocated |
|-------------- |----------- |-------------- |---------:|----------:|---------:|---------:|---------:|---------:|----------:|
| **EndToEndAsync** | **128**        | **1**             | **62.37 ms** |  **3.801 ms** | **0.208 ms** | **375.0000** | **375.0000** | **375.0000** |   **1.86 MB** |
| **EndToEndAsync** | **128**        | **32**            | **66.57 ms** | **16.030 ms** | **0.879 ms** | **375.0000** | **375.0000** | **375.0000** |   **1.86 MB** |
| **EndToEndAsync** | **1400**       | **1**             | **66.29 ms** | **22.658 ms** | **1.242 ms** | **375.0000** | **375.0000** | **375.0000** |   **1.86 MB** |
| **EndToEndAsync** | **1400**       | **32**            | **69.81 ms** |  **5.433 ms** | **0.298 ms** | **375.0000** | **375.0000** | **375.0000** |   **1.86 MB** |
