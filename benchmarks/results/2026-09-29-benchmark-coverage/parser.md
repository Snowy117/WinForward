```

BenchmarkDotNet v0.15.8, Linux NixOS 26.11 (Zokor)
AMD Ryzen 9 9955HX 2.50GHz, 1 CPU, 32 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method              | FrameBytes | Mean      | Error     | StdDev    | Allocated |
|-------------------- |----------- |----------:|----------:|----------:|----------:|
| **Ipv4UdpTryParse**     | **64**         | **11.486 ns** | **2.0721 ns** | **0.1136 ns** |         **-** |
| Ipv4UdpPayload      | 64         | 10.635 ns | 2.0201 ns | 0.1107 ns |         - |
| Ipv6UdpTryParse     | 64         | 10.583 ns | 4.6003 ns | 0.2522 ns |         - |
| Ipv6UdpPayload      | 64         |  9.871 ns | 1.2909 ns | 0.0708 ns |         - |
| Socks5UdpDecode     | 64         | 13.834 ns | 2.8477 ns | 0.1561 ns |         - |
| Socks5UdpEncodeSpan | 64         | 21.818 ns | 2.2887 ns | 0.1255 ns |         - |
| **Ipv4UdpTryParse**     | **512**        | **11.407 ns** | **1.7514 ns** | **0.0960 ns** |         **-** |
| Ipv4UdpPayload      | 512        | 10.722 ns | 6.2122 ns | 0.3405 ns |         - |
| Ipv6UdpTryParse     | 512        | 11.585 ns | 8.1156 ns | 0.4448 ns |         - |
| Ipv6UdpPayload      | 512        | 10.495 ns | 1.9718 ns | 0.1081 ns |         - |
| Socks5UdpDecode     | 512        | 13.282 ns | 5.3737 ns | 0.2945 ns |         - |
| Socks5UdpEncodeSpan | 512        | 28.591 ns | 6.7721 ns | 0.3712 ns |         - |
| **Ipv4UdpTryParse**     | **1514**       | **12.357 ns** | **2.5359 ns** | **0.1390 ns** |         **-** |
| Ipv4UdpPayload      | 1514       | 10.696 ns | 0.1469 ns | 0.0081 ns |         - |
| Ipv6UdpTryParse     | 1514       | 10.656 ns | 1.3631 ns | 0.0747 ns |         - |
| Ipv6UdpPayload      | 1514       |  9.767 ns | 1.2533 ns | 0.0687 ns |         - |
| Socks5UdpDecode     | 1514       | 13.174 ns | 2.7286 ns | 0.1496 ns |         - |
| Socks5UdpEncodeSpan | 1514       | 36.663 ns | 3.3776 ns | 0.1851 ns |         - |
