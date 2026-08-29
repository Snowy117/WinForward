```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method              | FrameBytes | Mean     | Error     | StdDev   | Gen0   | Allocated |
|-------------------- |----------- |---------:|----------:|---------:|-------:|----------:|
| **Ipv4UdpTryParse**     | **64**         | **34.34 ns** |  **0.375 ns** | **0.021 ns** |      **-** |         **-** |
| Ipv4UdpPayload      | 64         | 18.67 ns |  1.052 ns | 0.058 ns |      - |         - |
| Socks5UdpDecode     | 64         | 19.59 ns |  5.553 ns | 0.304 ns | 0.0024 |      40 B |
| Socks5UdpEncode     | 64         | 12.65 ns |  1.231 ns | 0.067 ns | 0.0033 |      56 B |
| Socks5UdpEncodeSpan | 64         | 21.45 ns |  1.428 ns | 0.078 ns |      - |         - |
| **Ipv4UdpTryParse**     | **512**        | **11.53 ns** |  **3.398 ns** | **0.186 ns** |      **-** |         **-** |
| Ipv4UdpPayload      | 512        | 18.88 ns |  4.683 ns | 0.257 ns |      - |         - |
| Socks5UdpDecode     | 512        | 19.24 ns | 11.753 ns | 0.644 ns | 0.0024 |      40 B |
| Socks5UdpEncode     | 512        | 40.82 ns | 10.881 ns | 0.596 ns | 0.0301 |     504 B |
| Socks5UdpEncodeSpan | 512        | 26.55 ns |  9.631 ns | 0.528 ns |      - |         - |
| **Ipv4UdpTryParse**     | **1514**       | **11.44 ns** |  **2.053 ns** | **0.113 ns** |      **-** |         **-** |
| Ipv4UdpPayload      | 1514       | 18.59 ns |  2.305 ns | 0.126 ns |      - |         - |
| Socks5UdpDecode     | 1514       | 19.17 ns |  2.419 ns | 0.133 ns | 0.0024 |      40 B |
| Socks5UdpEncode     | 1514       | 92.50 ns | 55.473 ns | 3.041 ns | 0.0904 |    1512 B |
| Socks5UdpEncodeSpan | 1514       | 42.31 ns |  1.427 ns | 0.078 ns |      - |         - |
