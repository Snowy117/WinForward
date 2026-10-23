# B6b 复核复跑索引（2026-10-23）

独立复核（未参与实现）在交付工作区上重跑，驱动为已安装的 ABI v7 构建
`4A8635C35A85F5DBF369857DAEAD03D82F63A03DE46F5F1005B62684FA55B8B3`（服务 RUNNING）。
原始输出保留在本目录；本 README 只做索引，结论见 `.trellis/tasks/10-23-fwd-b6-hardening/verification.md` §13。
复核后的第二轮收口（产品多 L 决策、no_ingress/歧义正测、旧尺寸 IOCTL、清理）见 §13.4；收口驱动
SHA-256 `EA271858D8A3AD12D7263B09A558C32F2035CAE27611A4A852820075D59C0A95`（本目录日志仍是
第一轮 4A86 构建的复跑）。

| 文件 | 内容 |
|---|---|
| `review-linux-check.log` | `tools/check.sh --full`：8 PASS / 0 FAIL。 |
| `review-dotnet-test.log` | 复核前 `dotnet run ... -- test`：227/227。 |
| `review-dotnet-test2.log` | 复核修复（StateCommand/DeviceLayout 注释与断言）后的 227/227。 |
| `review-abi2.log` | 修复后的 ABI 一致性：23/23。 |
| `review-verify-gates.log` / `review-verify-gates2.log` | `tools/userland/verify-gates.sh`：23 条检查全 PASS（19 变异全红）；修复后复跑仍 23/23。 |
| `review-core.log` | VM 复跑 `forwarded-tcp --phase core`：41 PASS / 0 FAIL；客户端 65536B、ACK=65537、SHA 一致。 |
| `review-multi.log` | VM 复跑 `forwarded-tcp --phase multi`：40 PASS / 0 FAIL；跨入口 + 同入口两 L，客户端同源端口并发的 SHA 验证见 `review-clients.log`。 |
| `review-hotswap.log` | VM 复跑 `forwarded-tcp --phase hotswap`：27 PASS / 0 FAIL；本次 generation 57→58（旧表项 57、新表项 58），旧连接 upstream 0 / 新连接 upstream 1。 |
| `review-udp-core.log` | VM 复跑 `forwarded-udp --phase core`：84 PASS / 0 FAIL；客户端 10/10、源地址恢复为 192.168.100.99:40250。 |
| `review-clients.log` | 复核时 Linux 客户端 stdout（core / multi / hotswap / udp core）。 |
| `review-state.log` | 补 `no_write_right`/`redirect_non_tcp` 后重建 Controller 的 StateCommand 输出。 |

注意：

- 复核修复只涉及 StateCommand 的两行计数、DeviceLayout 的 v7 偏移断言与文档；为验证 StateCommand，
  把改动的 3 个 C# 文件同步到 VM 的源码副本并 `dotnet build`，未重跑产品 `WfpController` 的两入口映射，
  也没有重跑 `gate.ps1`（B6c 范围）。
- 复核后 VM 恢复为 forwarded disabled / 空 permit 表 / 无 .98、.77.99 别名 / 无 wfprobe 残留进程；
  测试 firewall 规则（40400–40430、40999）与 `C:\workspace` 的 `wfk-b6b.tgz`、`b6b-product.json`
  仍保留，属于已记录的测试环境状态。
