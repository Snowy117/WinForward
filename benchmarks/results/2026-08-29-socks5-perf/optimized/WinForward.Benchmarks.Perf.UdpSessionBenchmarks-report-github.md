```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.51GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                             | Sessions | Mean       | Error       | StdDev     | Gen0      | Gen1      | Gen2      | Allocated   |
|----------------------------------- |--------- |-----------:|------------:|-----------:|----------:|----------:|----------:|------------:|
| **PopulateSessionsAsync**              | **1**        |   **1.928 ms** |   **1.3864 ms** |  **0.0760 ms** |    **3.9063** |         **-** |         **-** |    **89.56 KB** |
| PopulateSessionsNoopTransportAsync | 1        |   1.421 ms |   0.2117 ms |  0.0116 ms |         - |         - |         - |     6.95 KB |
| **PopulateSessionsAsync**              | **100**      |  **26.303 ms** |  **65.3544 ms** |  **3.5823 ms** |  **812.5000** |  **781.2500** |  **250.0000** |  **8812.58 KB** |
| PopulateSessionsNoopTransportAsync | 100      |   2.076 ms |   0.3424 ms |  0.0188 ms |   23.4375 |    7.8125 |         - |   385.67 KB |
| **PopulateSessionsAsync**              | **1000**     | **305.846 ms** | **242.2520 ms** | **13.2787 ms** | **7000.0000** | **6500.0000** | **1500.0000** | **89024.26 KB** |
| PopulateSessionsNoopTransportAsync | 1000     |  11.157 ms |   2.4193 ms |  0.1326 ms |  343.7500 |  281.2500 |  140.6250 |  5364.07 KB |
