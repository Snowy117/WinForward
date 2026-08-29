```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                | Sessions | Mean       | Error      | StdDev    | Gen0      | Gen1      | Gen2      | Allocated   |
|---------------------- |--------- |-----------:|-----------:|----------:|----------:|----------:|----------:|------------:|
| **PopulateSessionsAsync** | **1**        |   **1.797 ms** |  **0.3758 ms** | **0.0206 ms** |    **3.9063** |    **1.9531** |         **-** |    **90.01 KB** |
| **PopulateSessionsAsync** | **100**      |  **28.798 ms** |  **8.3147 ms** | **0.4558 ms** |  **812.5000** |  **781.2500** |  **250.0000** |  **8996.76 KB** |
| **PopulateSessionsAsync** | **1000**     | **310.423 ms** | **89.0634 ms** | **4.8819 ms** | **6000.0000** | **5000.0000** | **1000.0000** | **92228.92 KB** |
