```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.51GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method              | FrameBytes | Mean     | Error     | StdDev   | Gen0   | Allocated |
|-------------------- |----------- |---------:|----------:|---------:|-------:|----------:|
| **Ipv4UdpTryParse**     | **64**         | **11.92 ns** |  **3.444 ns** | **0.189 ns** |      **-** |         **-** |
| Ipv4UdpPayload      | 64         | 18.69 ns |  4.266 ns | 0.234 ns |      - |         - |
| Socks5UdpDecode     | 64         | 20.88 ns | 14.632 ns | 0.802 ns | 0.0024 |      40 B |
| Socks5UdpEncode     | 64         | 12.92 ns |  8.260 ns | 0.453 ns | 0.0033 |      56 B |
| Socks5UdpEncodeSpan | 64         | 21.53 ns |  2.640 ns | 0.145 ns |      - |         - |
| **Ipv4UdpTryParse**     | **512**        | **11.46 ns** |  **1.476 ns** | **0.081 ns** |      **-** |         **-** |
| Ipv4UdpPayload      | 512        | 18.76 ns |  5.685 ns | 0.312 ns |      - |         - |
| Socks5UdpDecode     | 512        | 19.60 ns |  7.108 ns | 0.390 ns | 0.0024 |      40 B |
| Socks5UdpEncode     | 512        | 41.38 ns | 11.580 ns | 0.635 ns | 0.0301 |     504 B |
| Socks5UdpEncodeSpan | 512        | 28.94 ns |  8.470 ns | 0.464 ns |      - |         - |
| **Ipv4UdpTryParse**     | **1514**       | **11.45 ns** |  **1.027 ns** | **0.056 ns** |      **-** |         **-** |
| Ipv4UdpPayload      | 1514       | 18.67 ns |  0.811 ns | 0.044 ns |      - |         - |
| Socks5UdpDecode     | 1514       | 19.36 ns |  1.681 ns | 0.092 ns | 0.0024 |      40 B |
| Socks5UdpEncode     | 1514       | 89.21 ns | 66.411 ns | 3.640 ns | 0.0904 |    1512 B |
| Socks5UdpEncodeSpan | 1514       | 43.65 ns |  3.251 ns | 0.178 ns |      - |         - |
