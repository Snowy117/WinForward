# 执行计划

1. 配置层加可选 `socks5` 段与校验。
2. `Relay/Socks5Client.cs` + 单测（用内存 loopback 假上游覆盖三种地址形态与错误码）。
3. 接进中继的出站路径；不配置时走原直连。
4. `tools/socks5/` 测试替身 + 计数。
5. 端到端：VM 上起替身（loopback），跨机 target 在宿主 40000，跑 1 MiB 校验和。
6. 门禁：`wf_check`、C# 全部用例、有效行。

不要碰 `src/driver/`、`src/common/`、`tests/host/`。
