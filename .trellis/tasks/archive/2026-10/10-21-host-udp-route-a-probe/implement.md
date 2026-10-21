# 执行计划：Host UDP 路线 A 最小探针

> 顺序执行；每步先构建再跑实验，失败就停在该步并记录，不跳步。
> 所有 VM 命令在 `windows-vm` 会话里执行；本机没有 `wf.local.json`，路径用会话里的实际仓库根。

## 0. 准备

- [ ] `windows-vm status` 确认 VM 运行；`windows-vm connect` 建 tmux 会话。
- [ ] 确认 VM 上的仓库根（`C:\...\WinForward.Kernel`）与本机工作区一致；用 `windows-vm <session> up`
      上传本轮改动的文件（或整目录）。
- [ ] 在 VM 上跑一次基线构建/安装，确认现有驱动能加载：
      `pwsh -File tools\windows\load-driver.ps1 -Root <repo>`
- [ ] 本机跑 `tools/check.sh --fast`，确认工作区干净、格式基线通过。

## 1. 驱动实验骨架

- [ ] `probe_ioctl.h`：追加 `udp_probe_config` / `udp_probe_stats` 与 0x880/0x881。
- [ ] 新增 `udp_probe.h` / `udp_probe.cpp`：配置/统计状态、映射表、`udp_probe_classify_redirect`、
      `udp_probe_classify_return`、注册/注销入口。
- [ ] `probe_driver.h`：`probe_state` 加实验配置/统计/锁/表（或把表放进 `udp_probe.cpp` 的静态状态，
      只暴露接口，减少对 `probe_state` 的侵入）。
- [ ] `probe_driver.cpp`：`DriverEntry` 注册实验 callout；卸载反序注销。
- [ ] `probe_classify.cpp`：在 TCP 早退前插入 UDP redirect 分支，默认关闭。
- [ ] `wfprobe.vcxproj`：加入 `udp_probe.cpp`。
- [ ] 构建通过；`tools/check.sh --fast` 通过；现有 TCP `gates` 不退化（至少 `selftest` + `gates`）。

验证：

```powershell
pwsh -File tools\windows\load-driver.ps1 -Root <repo>
wfprobe.exe control --enabled 1 --port <tcp-listener>
wfprobe.exe selftest --remote <remote>
wfprobe.exe gates
```

## 2. 用户态探针命令

- [ ] `UdpSockets.cs`：`IP_WFP_REDIRECT_CONTEXT=70`、`WSARecvMsg` P/Invoke、cmsg 遍历与打印。
- [ ] `UdpProbe.cs`：配置/统计 IOCTL 封装；`Protocol.cs` 加结构镜像与尺寸断言；`WfpDevice` 加方法。
- [ ] `UdpListenCommand.cs` / `UdpClientCommand.cs` / `UdpRelayCommand.cs`。
- [ ] `Program.cs`：加 `udp-probe`、`udp-listen`、`udp-client`、`udp-relay` 与 usage。
- [ ] `dotnet build tools/wfprobe` 通过；`udp-listen` 能在本机/VM 上绑定并打印 PID。

验证：

```powershell
wfprobe.exe udp-listen --bind 0.0.0.0 --port 5555 --count 1 --timeout-ms 5000
wfprobe.exe udp-client --remote 1.2.3.4:443 --mode unconnected --payload hello --timeout-ms 1000
```

## 3. E0 基线

- [ ] 实验关闭，`udp-listen` 等 5s；`udp-client` 发 3 个包。
- [ ] 记录：listener 收到 0 个；`wfprobe stats` 的 `permitted_non_tcp` 增长。
- [ ] 产出 `research/logs/E0-baseline.txt`。

## 4. E1 / E2：redirect 与 cmsg

- [ ] 启动 `udp-listen --port 5555 --count 1`，拿到 PID。
- [ ] `udp-probe --enable 1 --port 5555 --pid <pid> --return-mode off`。
- [ ] E1：`udp-client --remote 1.2.3.4:443 --mode unconnected`；记录 listener 的 source + cmsg hex
      与解析结果。
- [ ] E2：`udp-client --remote 1.2.3.4:443 --mode connected`；记录客户端 send/recv 结果与错误码。
- [ ] 若 E1 的 cmsg 解析不出原始目的，打印 raw hex 并对照 `redirect_context` 布局，记录实际形态。
- [ ] 产出 `research/logs/E1-unconnected.txt`、`E2-connected.txt`。

## 5. E3：回程基本闭环（unconnected）

- [ ] `udp-relay --port 5555 --count 1 --linger-ms 1000`，拿到 PID。
- [ ] `udp-probe --enable 1 --port 5555 --pid <pid> --return-mode off --layer datagram`。
- [ ] `udp-client --remote 1.2.3.4:443 --mode unconnected --payload ping`；确认客户端收到 `ping`。
- [ ] 若收不到：用 `events` / `DbgPrint` 确认 relay 是否收到、是否回包。
- [ ] 产出 `research/logs/E3-relay-unconnected.txt`。

## 6. E4：回程源改写（connected，生死点）

- [ ] `udp-relay --port 5555 --count 1 --linger-ms 2000`，`udp-probe` 设 `return-mode observe`。
- [ ] connected 客户端发 `ping`；记录 relay 回包后，驱动回程层是否看到（`return_seen`）。
  - 若 `return_seen == 0`：换 `--layer recv` 重试；仍为 0 则记录"该层不可见"，进入 inject 兜底。
- [ ] 设 `return-mode rewrite-inline`；connected 客户端发 `ping`，记录是否收到、`recvfrom` 源地址。
- [ ] 若 inline 失败：设 `return-mode rewrite-inject`，重复；记录注入完成状态与客户端结果。
- [ ] 每一步都记录 `udp_probe_stats` 与客户端错误码；产出 `research/logs/E4-return-*.txt`。

## 7. E5：生命周期与清理

- [ ] 用脚本循环 200 次：`udp-client` 单包 + 重启 listener / 重新 enable。
- [ ] 记录 `pooltag --tag WfPr` 的 outstanding 前后值；确认 context 不增长。
- [ ] `sc stop wfprobe` / `sc delete wfprobe`；确认无蓝屏、无残留服务。
- [ ] 重新安装驱动，确认可重复加载。
- [ ] 产出 `research/logs/E5-lifecycle.txt`。

## 8. E6（可选）

- [ ] `8.8.8.8:53` 作为第二目标；确认 cmsg/映射按 remote 区分。
- [ ] dual-mode：客户端用 `[::ffff:1.2.3.4]:443` 或 IPv6 listener，记录是否走 V6 层（本任务不做完整 IPv6）。


## 8b. E7：connected UDP workaround 探索（仅当 E2/E4 失败时执行）

- [ ] W1：`udp-probe --redirect-target local` 重跑 E2；记录 listener 是否收到、客户端错误码。
- [ ] W2：`udp-probe --context off|on --handle off|on` 四组对照重跑 E2；记录差异。
- [ ] W3：`udp-relay --attach-records` 重跑 E4；记录客户端是否接受回包。
- [ ] W4：实验驱动加 `--fallback packet`（connected flow 走 `DATAGRAM_DATA` clone+inject），
      重跑 E3/E4；记录兜底是否可用、每包成本。
- [ ] 每组都保存 `research/logs/E7-W<N>-*.txt`，并在 findings §9 给出可用性矩阵。
- [ ] 若 W4 可用，明确记录"A 为主 + B 兜底 connected"的两套数据面成本，供产品决策。

## 9. findings 与收尾

- [ ] 写 `research/route-a-findings.md`：E0~E6 的结论、证据、复现命令、剩余风险。
- [ ] 给出 GO / CONDITIONAL GO / NO-GO，并写明若 GO 时下一步（规则表 UDP、正式 ABI、回程映射设计）。
- [ ] 若结论是 NO-GO 或 CONDITIONAL，写明切到路线 B 的触发条件与需要补的实验。
- [ ] 清理实验代码（按用户决定删除或转正）；跑 `tools/check.sh --full` 与 VM `gate.ps1`。

## 验证命令速查

```powershell
# 构建/安装（快速迭代）
pwsh -File tools\windows\load-driver.ps1 -Root <repo>
# 完整门禁（收尾时）
pwsh -File tools\windows\gate.ps1 -Root <repo> -Remote <remote>
# 驱动统计
wfprobe.exe stats
wfprobe.exe pooltag --tag WfPr
# 实验
wfprobe.exe udp-probe --enable 1 --port 5555 --pid <pid> --return-mode observe --layer datagram
wfprobe.exe udp-listen --bind 0.0.0.0 --port 5555 --count 1 --timeout-ms 5000
wfprobe.exe udp-client --remote 1.2.3.4:443 --mode connected --payload ping --timeout-ms 1000
wfprobe.exe udp-relay --bind 0.0.0.0 --port 5555 --count 1 --linger-ms 2000
```

## 风险文件与回滚点

- 高风险：`probe_classify.cpp`（动到 TCP 路径）、`probe_driver.cpp`（注册/卸载顺序）。
- 回滚点：每完成第 1、2 步各打一个 git stash / 分支；实验失败时只回滚 `src/driver` 与
  `tools/wfprobe` 的实验改动，不动 `.trellis` 记录。
- 不在本任务里 `git commit`；提交由主会话在评审后统一做。
