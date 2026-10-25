# Implement — Forwarded IPv6 数据面 E2E

执行顺序：环境准备 → 夹具 → 客户端 → 门禁 → v4 回归 → v6 单阶段 → 全量 → 清理与文档。
任何一步失败先回到对应小步修复，不继续叠加。

## 0. 前置检查与基线

- [ ] 确认 VM 运行、Internal `ms_tcpip6` 启用、现有驱动 ABI 为 v8：
  ```bash
  windows-vm status
  windows-vm <session> run 'Get-NetAdapterBinding -Name Internal -ComponentID ms_tcpip6 | Format-List Enabled'
  windows-vm <session> run 'C:\workspace\WinForward.Kernel\tools\wfprobe\bin\Release\net11.0\wfprobe.exe stats'
  ```
  若驱动不是当前源码构建，先按 `tools/windows/gate.ps1` / `WfBuild.psm1` 构建安装；
  本任务预期不改驱动，但 E2E 必须和当前源码同批。
- [ ] 记录环境快照：Windows IPv6 地址、`B6C-ENV-*` 规则、Linux eth0/eth1 地址：
  ```bash
  windows-vm <session> run 'Get-NetIPAddress -AddressFamily IPv6 | Format-Table InterfaceIndex,InterfaceAlias,IPAddress,AddressState -AutoSize | Out-String -Width 200'
  windows-vm <session> run 'Get-NetFirewallRule -DisplayName "B6C-ENV-*" | Format-Table DisplayName,Enabled,Action -AutoSize | Out-String -Width 200'
  ip -6 -br addr show dev eth0; ip -6 -br addr show dev eth1
  ```
- [ ] 跑一次现有 v4 门禁基线（日志放任务目录）：
  ```bash
  python3 tools/linux/forwarded_gate.py \
    --log-dir .trellis/tasks/10-24-fwd-ipv6-e2e/verification/baseline-v4
  ```
  预期 `ALL PASS`。失败先修环境，不带病改代码。

## 1. prepare / cleanup v6 支持

文件：`tools/windows/prepare-b6c-forwarded.ps1`。

- [ ] 增加 `-WithIpv6`：
  - 发现 Internal/External 主链路本地（`Preferred`，排除 `fe80::98/99/199`）。
  - 添加 `fe80::98`（Internal L2）、`fe80::99`（Internal target）、
    `fe80::199`（External target）；轮询到 `Preferred`。
  - 增加 `B6C-ENV-V6-*` 防火墙规则（40250/40251 TCP+UDP、40400 TCP+UDP、
    40420 TCP、40600 UDP、41400 TCP）。
  - 输出 `B6C-V6-READY internal_l1=... internal_l2=... internal_target=... external_l1=... external_target=...`。
- [ ] `-Remove` 删除上述地址（`netsh interface ipv6 delete address`）与
  `B6C-ENV-V6-*` 规则；不删除现有主链路本地。
- [ ] Internal `ms_tcpip6` 禁用时明确 throw，不静默修改绑定。
- [ ] 手工验证：
  ```bash
  windows-vm <session> script ./tools/windows/prepare-b6c-forwarded.ps1 -WithIpv6
  windows-vm <session> run 'Get-NetIPAddress -InterfaceIndex 5 -AddressFamily IPv6 | Format-Table IPAddress,AddressState -AutoSize | Out-String'
  windows-vm <session> script ./tools/windows/prepare-b6c-forwarded.ps1 -Remove
  windows-vm <session> run 'Get-NetIPAddress -InterfaceIndex 5 -AddressFamily IPv6 | Format-Table IPAddress,AddressState -AutoSize | Out-String'
  ```
  确认增删干净、主链路本地保留。此步是环境回滚点 1。

## 2. wfprobe 共享 helper 与 TCP suite

文件：`tools/wfprobe/TestSupport.cs`、`tools/wfprobe/ForwardedTcpSuite.cs`。

- [ ] `SocketHelpers` 增加 `WithScope` / `SameAddress`。
- [ ] TCP suite 增加 v6 `--no-alias` 跳过；`RunAsync` 与 `RunMultiAsync` 共用 helper。
- [ ] 增加 family-aware `BindTcpAny`；core/upstreams/fail 的 IPv4 listener 行为不变。
- [ ] `BindOn` 对链路本地补 scope；multi 的 v6 alias 跳过；地址比较用 `SameAddress`。
- [ ] `RunMultiAsync` 支持传入 L1/L2/External L 与两个 target；默认 v4 值不变。
- [ ] 编译：
  ```bash
  windows-vm <session> run 'dotnet build C:\workspace\WinForward.Kernel\tools\wfprobe\wfprobe.csproj -c Release -v:q'
  ```
  0 warning / 0 error。此步是编译回滚点 1。

## 3. wfprobe UDP suite

文件：`tools/wfprobe/ForwardedUdpSuite.cs`。

- [ ] `BindUdp(port, family)`、`AnyFor(family)`、`BindEphemeralUdp(family)`。
- [ ] `TryBindDecoy(remote, port, ifIndex)` family-aware + `WithScope`。
- [ ] core / socks5 / capacity / reject / fail 使用 family-aware listener/any。
- [ ] `FindAdapterIndex` 用 `SameAddress`。
- [ ] 重新编译 wfprobe；确认 v4 单元/宿主套件不受影响。

## 4. Linux 客户端

文件：`tools/linux/forwarded_tcp_client.py`、`forwarded_udp_client.py`、
`forwarded_tcp_b6b.py`。

- [ ] TCP core 客户端：target/verify 支持 `[v6]:port`、裸 v6 + `%scope`；内容/SHA/ACK
      校验不变。
- [ ] UDP 客户端：按 target 族建 socket；v6 bind `("::", port)`；`getaddrinfo` 4 元组
      sendto；回程源比较忽略 scope；v6 sweep 明确报不支持。
- [ ] B6b multi 客户端：`--bind-address` 可重复；按 target 族建 socket；bind 支持
      `%iface`；同一源端口可跨接口；v4 单 bind 行为保持。
- [ ] 语法检查：
  ```bash
  python3 -m py_compile tools/linux/forwarded_tcp_client.py \
    tools/linux/forwarded_udp_client.py tools/linux/forwarded_tcp_b6b.py
  ```

## 5. 门禁 v6 阶段

文件：`tools/linux/forwarded_gate.py`、`tools/linux/forwarded_gate.md`。

- [ ] 增加 `--include-v6`，默认路径不变。
- [ ] prepare 传 `-WithIpv6`，解析 `B6C-V6-READY`；只读发现 Linux eth0/eth1 链路本地。
- [ ] 新增 `tcp-core6` / `tcp-upstreams6` / `udp-core6` / `udp-socks5-6` /
      `tcp-multi6` 五个 phase，参数按 design.md §4。
- [ ] `finally` 无条件调用 `prepare -Remove`；v6 资源删除不依赖开关。
- [ ] 文档写明前置条件、地址发现、phase 列表与回滚命令。
- [ ] `--no-verify` guard 仍拒绝（现有 `run_phase` 检查）。

## 6. v4 回归

- [ ] 重新跑 v4 门禁（不带 `--include-v6`）：
  ```bash
  python3 tools/linux/forwarded_gate.py \
    --log-dir .trellis/tasks/10-24-fwd-ipv6-e2e/verification/v4-regression
  ```
- [ ] 额外跑一次 v4 multi + hotswap（B6b 夹具），确认客户端 `--bind-address` 改动
      没破坏 v4：
  ```bash
  # 具体启动命令参考 B6b verification.md §13；只要求 multi 42 PASS / hotswap 27 PASS
  ```
- [ ] Linux 侧回归：
  ```bash
  tools/check.sh --full
  dotnet test
  ```
  若只改 tools/linux 与 tools/wfprobe，C++/userland 测试应保持原通过数；记录实际数字。

## 7. v6 单阶段验证

按顺序跑，每一步失败先定位，不要直接跑全量。建议使用 gate 的 phase 选择或手工命令；
手工命令示例（suite 侧参数由 gate 生成，客户端 target 带 `%eth0`）：

- [ ] `tcp-core6`：64 KiB 双向一致；redirect 表 server=`fe80::99`、L=`fe80::8ca8...`；
      客户端 verify SHA/ACK 通过。
- [ ] `tcp-upstreams6`：两个假 SOCKS5 各一次 CONNECT，upstream 0/1 正确。
- [ ] `udp-core6`：10 个 datagram；客户端源地址=`fe80::99:40250`；decoy 无数据。
- [ ] `udp-socks5-6`：两个 ASSOCIATE；回程源地址/载荷通过。
- [ ] `tcp-multi6`：4 条连接；跨入口 ingress 5/14、同入口两个 L；
      `redirect_multi_l >= 1`、`return_ambiguous=0`、`no_ingress=0`。
- [ ] 每个 phase 的 suite 日志、客户端日志保存到
      `.trellis/tasks/10-24-fwd-ipv6-e2e/verification/<phase>/`。
- [ ] 反向验证（至少两项）：
  - 把 `tcp-core6` 的 target 改成不存在的地址，gate 必须以 client/suite 失败退出；
  - 把 `tcp-multi6` 的 External target 改错，gate 必须 FAIL；
  - 确认 `--no-verify` 仍被 gate 拒绝。

## 8. v6 全量门禁与证据

- [ ] 全量：
  ```bash
  python3 tools/linux/forwarded_gate.py --include-v6 \
    --log-dir .trellis/tasks/10-24-fwd-ipv6-e2e/verification/full-v6 \
    2>&1 | tee .trellis/tasks/10-24-fwd-ipv6-e2e/verification/full-v6/run.log
  ```
  预期 v4 + v6 全部 `PASS`，最后 `ALL PASS`。
- [ ] 记录驱动 SHA-256：
  ```bash
  windows-vm <session> run '(Get-FileHash C:\Windows\System32\drivers\wfprobe.sys -Algorithm SHA256).Hash'
  ```
- [ ] 写 `.trellis/tasks/10-24-fwd-ipv6-e2e/verification.md`：结果表、命令、日志相对
      路径、驱动 SHA、v6 地址、失败/跳过项。

## 9. 清理与环境恢复

- [ ] 确认 suite 已恢复 `rules=0 fallback=permit config=disabled`，无 wfprobe 进程。
- [ ] 执行 `prepare-b6c-forwarded.ps1 -Remove`，确认：
  - Internal/External 只剩原始地址；
  - `B6C-ENV-*` 规则为 0；
  - Linux eth0/eth1 地址未变化；
  - 没有残留 listener（40400/40250/40251/40420）。
- [ ] 如中断，手工清理：
  ```powershell
  netsh interface ipv6 delete address "Internal" fe80::98
  netsh interface ipv6 delete address "Internal" fe80::99
  netsh interface ipv6 delete address "External" fe80::199
  Get-NetFirewallRule -DisplayName "B6C-ENV-*" | Remove-NetFirewallRule
  ```
- [ ] 记录清理结果到 `verification.md`。

## 10. 文档与收尾

- [ ] 通过后更新：
  - `src/driver/README.md`：native IPv6 TCP/UDP forwarded happy path、多 L、两入口
    已验证；保留 UDP 多 L / atomic fragment / raw 分片边界。
  - `src/userland/README.md`：同步边界；注明 WfpController v6 L 自动解析仍未做。
  - `.trellis/spec/backend/wfp-architecture.md` §7.16/§15：新增本次证据与剩余边界。
  - `tools/linux/forwarded_gate.md`：`--include-v6` 使用说明。
- [ ] 若 E2E 暴露驱动缺陷：单列修复、补 host 单测、重新跑 v4/v6；ABI 保持 v8。
- [ ] 运行最终质量检查：`tools/check.sh --full`、`dotnet test`、wfprobe 构建；
      记录实际通过数。
- [ ] 提交前按 Trellis Phase 3 流程更新 spec、写 journal、归档任务。

## 风险文件与回滚点

| 文件 | 风险 | 回滚 |
|---|---|---|
| `tools/windows/prepare-b6c-forwarded.ps1` | 改宿主机地址/防火墙 | 先跑一次 add→remove 验证；`-Remove` 清理 |
| `tools/wfprobe/ForwardedTcpSuite.cs` | v4 core/upstreams/multi 回归 | 编译回滚点；v4 gate + B6b multi 复跑 |
| `tools/wfprobe/ForwardedUdpSuite.cs` | v4 core/socks5/capacity/reject 回归 | v4 gate 复跑 |
| `tools/linux/forwarded_gate.py` | 默认 gate 行为变化 | `--include-v6` 开关隔离；v4 回归 |
| Linux 客户端脚本 | v4 路径被破坏 | 默认参数保持 v4；单独跑 v4 phase |

## 完成前检查

- [ ] PRD 验收项逐条有证据。
- [ ] 目标地址与 L 不同，回程 SNAT 真的被观察到。
- [ ] 没有只靠计数器 PASS 的用例。
- [ ] v4 默认门禁仍全绿。
- [ ] 环境与地址恢复干净。
- [ ] 文档口径与证据一致，未把“未验证”写成“已支持”。
