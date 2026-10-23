# B6a 证据索引

| 文件 | 内容 |
|---|---|
| `final-core.log` / `final-frag4.log` / `final-frag6.log` / `final-fraghost.log` / `final-tcp.log` / `final-capacity.log` | 最终驱动 E2E：单条 UDP、IPv4 raw 9 片、IPv6 1280 MTU 9 片、host 分片、TCP ACK、生产 4096 capacity。 |
| `final-fragment-permit-first.log` | permit 规则排在 redirect 前（负测）：9 片全部 `fragmented=9/blocked=9/created=0`。 |
| `final2-*.log` | 最终重构（helper 移到头文件）后的复跑：basic/permit-first/host-same-remote/core/tcp 全 PASS。 |
| `final3-*.log` | 第三轮最终二进制（SHA 5E7F…104E）复跑：IPv4/IPv6 fragment、permit-first、host 同 remote、core、tcp、fail 清理、U11。 |
| `reject-final.log` / `reject-final-client.out` / `reject-final-fake.log` / `reject-final-controller.log` / `reject-config.json` | 最终 reject harness：客户端 0/3、associations=3/failures=3/back=0/decoy=0，原始 controller 日志与配置。 |
| `udp-rules-final3-1.log` / `-2.log` | 最终 `udp-rules` 连续两次 PASS（含 U11 事件窗口正向断言）。 |
| `final4-*.log` | 第四轮交付构建（SHA 206C…BA6A，无测试 hook）的 core/IPv4/IPv6 fragment/capacity/U7 single/fail-tcp 复跑。 |
| `final5-*.log` | 第五轮交付构建（SHA DACF…C606，无 hook/无 RtlWrite 诊断）的 core/fragment/permit/host/fail 复跑。 |
| `udp-rules-final5-1.log` / `-2.log` | U13 改为事件归属后连续两次 `udp-rules` PASS。 |
| `stop-diag-final.log` | stop 诊断：`InjectionDelayEffectiveUs`/`LastStopCompletionDelayUs`/`LastStopDelayedCompletions`/entry/after 原值。 |
| `final-fragment-host-same-remote.log` | 同一 remote IP、不同端口（`.99:40600`）host 分片正测：重组收到 544B，forwarded 计数 0。 |
| `b6-u7a5.out` / `b6-u7b5.out` / `u7-watchdog.log` | U7 旧看门狗跨 suite 回归：A(gen31) 看门狗被 B(gen32) 跳过。 |
| `u7-single-no-verify.log` | U7 single/no-verify 复跑：窗口校验与 decoy 硬断言 PASS。 |
| `u7-decoy-forced-watchdog-repro.out` | 旧构建强制提前看门狗的原帧 permit 复现（decoy bytes=64）。 |
| `reject-harness.log` | `tools/windows/forwarded-udp-reject-e2e.ps1` 的产品级 reject 输出：`REJECT-E2E-RESULT=PASS`。 |
| `reject-harness-client.out` | reject 客户端 0/3（无回程）。 |
| `reject-harness-fake.log` | fake SOCKS5 3 次 `associate-failed rep=7`。 |
| `reject-controller.log` / `reject-fake-socks5.log` / `reject-client.out` | 首轮人工 reject E2E 原始日志（同一产品路径）。 |
| `udp-rules-final-1.log` / `-2.log` | host UDP 门禁连续两次 `verdict=PASS failures=0`（U9/U11 修复 + U11 事件窗口正向断言后）。 |
| `stop-reload-loop.log` | 5 次 `sc stop`/`sc start`：均 RUNNING，`WfIj` 仅当前池 1 个 outstanding，无累积泄漏。 |
| `b6-stop-diagnostics`（见 `stop-fault-injection.log`） | 故障注入诊断：`LastStopCompletionDelayUs`、`LastStopDelayedCompletions=2`；stop entry 采样受控制通道延迟限制，详见 `verification.md` §5。 |
| `gate-b6a-final.log` | 最终 `gate.ps1` 完整日志（GATE-RESULT、PREfast、host 套件、userland-e2e、卸载池检查）。 |
| `gate-b6a-refactor.log` | 最终重构后再次执行的 `gate.ps1` 日志：GATE-RESULT=PASS、WfPr 100→100、WfIj 1→0、WfTr 5→0。 |
| `gate-b6a-round3.log` | 第三轮最终源码（SHA 5E7F…104E）的 `gate.ps1`：GATE-RESULT=PASS、WfPr 120→120、WfIj 1→0、WfTr 5→0。 |
| `gate-b6a-round4b.log` | 交付源码（无 hook）的 gate：GATE-RESULT=PASS，gate 构建 SHA D3A1…7EFF，WfPr 137→137、WfIj 1→0、WfTr 5→0。首次 round4 的 U13 flake 原始日志未保留，摘录见 `verification.md` §11。 |
| `gate-b6a-r5-1.log` / `gate-b6a-r5-2.log` | 第五轮交付源码连续两次 gate：均 GATE-RESULT=PASS；gate 构建 SHA 5067…4B37 / B572…4FAC；U13 修复后无 flake。 |
| `udp-rules-final4-standalone.log` | gate 首次 U13 flake 后单独重跑 `udp-rules`：verdict=PASS。 |
| `b6-core-a.out` / `b6-frag4.out` / `b6-frag6.out` / `b6-fraghost.out` / `b6-capacity.out` / `b6-tcp2.out` / `b6-u7a5.out` / `b6-u7b5.out` | 修复前/中期的原始运行，保留对照。 |
## B6b 证据（ABI v7）

| 文件 | 内容 |
|---|---|
| `b6b-abi.log` / `b6b-dotnet-test.log` / `b6b-verify-gates.log` | ABI 23/23、dotnet 227/227、verify-gates 23/23。 |
| `b6b-linux-check.log` | `tools/check.sh --full` 8 PASS / 0 FAIL。 |
| `b6b-tcp-core.log` / `b6b-tcp-multi.log` / `b6b-tcp-hotswap.log` | forwarded TCP core / multi / hotswap；multi 覆盖两入口与同入口两 L，hotswap 覆盖旧表项 generation/upstream 保留。 |
| `b6b-udp-core.log` | forwarded UDP core 84 PASS（10/10、源地址恢复）。 |
| `b6b-host-suites.log` | host rules / forwarded-rules / selftest / gates / udp-rules，EXIT 全 0。 |
| `b6b-product-udp-reject.log` / `b6b-product-controller.log` | 产品 Controller + `ForwardedUdpRelay` 的 reject E2E 与 ingress 日志。 |
| `b6b-state.log` / `b6b-final-state.log` | StateCommand 输出与测试后 VM 状态。 |
| `review/` | 第一轮独立复核复跑（Linux 门禁、TCP core/multi/hotswap、UDP core）与客户端 stdout，见 `review/README.md`。 |
| `review2/` / `b6b-recheck-*` | 第二轮收口与独立复核：core 44、multi 42、hotswap 27（含 post-swap）、noingress 18、UDP 85、vm-check 86、产品 reject PASS，见 `review2/README.md`。 |

说明：

- IPv4 raw 9 片通过 rootless podman `--network=host --cap-add=NET_RAW` 构造；IPv6 用 1280 MTU +
  `IPV6_PMTUDISC_DONT` 的内核分片（宿主 raw IPv6 HDRINCL 交替 EPERM）。
- 产品 reject 必须走 `WinForward.Controller` + `ForwardedUdpRelay`；suite 的 `reject` phase 是进程内
  夹具，不能单独作为产品证据。
- 所有 `ASSERT PASS/FAIL` 与客户端 stdout 原样保留。
