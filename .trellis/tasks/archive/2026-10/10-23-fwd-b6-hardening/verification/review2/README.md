# B6b 第二轮最终复核（2026-10-23）

独立复核（未参与实现）。最终驱动 ABI 7：
`EA271858D8A3AD12D7263B09A558C32F2035CAE27611A4A852820075D59C0A95`，服务 RUNNING；
复核结束时 forwarded disabled / 空表（generation=36、rules=0、enabled=0、ingresses=0；产品 reject 后另做一次 legacy restore-only 清掉遗留的 ingress 行）。

| 检查 | 结果 | 证据 |
|---|---|---|
| Linux `tools/check.sh --full` | 8 PASS / 0 FAIL | `final-linux-check.log` |
| dotnet 单测 | 227/227 | `final-dotnet.log` |
| `verify-gates.sh` | 23/23（19 变异全红） | `final-gates.log` |
| VM `vm-check` | 86 checks / 0 failures（含 20B 拒绝、24B/40B 接受、56B 输出拒绝、stats 400/448/456、392 拒绝） | `final-vm-check.log` |
| VM `forwarded-tcp --phase core` | 44 PASS / 0 FAIL（含 legacy 24B→64B 输出、version=1、server 回读） | `final-core.log` |
| VM `forwarded-tcp --phase multi` | 42 PASS / 0 FAIL（两入口、同入口两 L、24B/任意 L 查询 NOT_FOUND 且不增 ambiguous） | `final-multi.log` |
| VM `forwarded-tcp --phase hotswap` | 27 PASS / 0 FAIL（旧连接换表后继续收发 4096B、新连接 2048B） | `final-hotswap.log` |
| VM `forwarded-tcp --phase noingress` | 18 PASS / 0 FAIL（只配 Internal、规则匹配 External；外部 SYN 被 block，created=0） | `final-noingress.log` |
| VM `forwarded-udp --phase core` | 85 PASS / 0 FAIL（10/10、源地址恢复、`udp_return_miss_blocked=0`） | `final-udp-core.log` |
| 产品 UDP reject E2E（Controller + 真实 relay + fake rep=7） | `REJECT-E2E-RESULT=PASS`；客户端 0/3、decoy=0、relayed=0/back=0、associations=2/failures=2 | `final-product-reject.log`、`final-product-reject-controller.log` |
| 客户端 stdout | core/multi/hotswap/udp 全部 ok=1；noingress 连接超时（预期） | `final-clients.log` |

复核结论：**通过（B6b）；B6c 的 gate.ps1/PREfast 与 A5 成本不在本次范围。**
仍按已写明的边界接受：产品每 ifindex 一个 L（同 ifindex 多 L 仅驱动/夹具）；产品两入口未 E2E；
UDP 多 L/热替换未单独 E2E；IPv6 多入口与 LWF 未验证；data-path ambiguous 是防御性路径；
混合 NBL 的 `udp_return_miss_blocked` 未构造 E2E（代码审查+计数路径）。

环境：B6b 的 `wf-b6b-*` 防火墙规则已用 `prepare-b6b-forwarded.ps1 -Remove` 清 0；
`.98/.77.99` 别名 0、无 wfprobe 残留进程；`C:\workspace` 的 B6b 临时件已清。
B6a 保留的 `B6-ENV-*` 规则、`wf-b6-reject.json`、`wfprobe-final.sys`/`wfprobe-fault.sys` 不在本次清理范围。
未 commit/push；未 reset VM。
