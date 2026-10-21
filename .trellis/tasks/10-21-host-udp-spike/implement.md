# 执行计划：Host UDP Spike

> 只做研究；每步记录命令与输出。临时改动集中在一个临时分支/补丁里，结束前还原。

## 0. 环境

- [ ] `windows-vm status`；未运行则 `windows-vm restart`。
- [ ] `windows-vm connect` 拿 tmux 会话名；`windows-vm <session> idle`。
- [ ] 确认 VM 上的仓库根（优先 `C:\workspace\WinForward.Kernel`；没有就用
      `Get-ChildItem C:\workspace -Directory` 或 `Get-ChildItem C:\ -Directory` 找）。
- [ ] 同步本机当前 HEAD 到 VM。最小同步：`git archive` 导出整包，或按文件
      `windows-vm <session> up ./<file> '<vm>/<file>'`；驱动构建需要整个仓库，优先整包。
- [ ] VM 上构建/安装基线：
      `pwsh -File tools\windows\load-driver.ps1 -Root <vm-repo>`
      确认 `sc query wfprobe` 为 RUNNING；`wfprobe.exe stats` 可读。

## 1. S1：classify 频次

- [ ] 启动 `wfprobe udp-relay --bind 0.0.0.0 --port 5555 --count 1 --linger-ms 30000`，
      记 `LISTENER-PID`。
- [ ] `wfprobe control --enabled 1 --port 5555 --listener-pid <pid>`（四道闸需要 listener 配置）。
- [ ] `wfprobe udp-probe --enable 1 --port 5555 --pid <pid> --return-mode off`。
- [ ] `wfprobe udp-client --remote 1.2.3.4:443 --mode unconnected --count 10 --timeout-ms 200`。
- [ ] 读 `wfprobe udp-probe-stats`；重复 `--count 1` / `--count 10` / 两个不同远端各 1 条。
- [ ] 记录：`seen`、`redirected`、`table` 与 datagram 数/关联数的关系。
- [ ] 清理：relay 结束或 Ctrl-C；`wfprobe udp-probe --enable 0`。

## 2. S2：context 生命周期

- [ ] 记录基线：`wfprobe pooltag --tag WfPr`（null 视为零基线）。
- [ ] 启 listener + `udp-probe`，发 N 条（建议 20 条、至少 5 个不同远端端口/地址）。
- [ ] 立刻、1s、5s、30s 各读一次 `pooltag --tag WfPr`；记录 allocs/frees/outstanding。
- [ ] 关闭客户端进程/relay；再按同样节奏读；`sc stop wfprobe` 后再读一次。
- [ ] 记录：context 是否随 datagram 线性增长、是否在关闭 socket 后释放、是否要等
      WFP idle、`sc stop` 后是否归零。
- [ ] 清理：`sc start wfprobe`（如果停了）；重新跑 `load-driver.ps1` 或保持服务可用。

## 3. S3：UDP block 的 auth 行为（临时改动）

- [ ] 临时改 `src/common/rule_blob.h`：加 `rule_protocol_udp = 17`，让
      `is_known_protocol` 接受 17。仅本地/VM，不提交。
- [ ] 临时在 `tools/wfprobe` 加一个 `udp-blockprobe` 命令（或给 `BlockProbe` 加 `--udp`）：
      - `BlobBuilder.Build(new[] { new TestRule { Action = RuleActionBlock,
        PortStart = 50003, PortEnd = 50003, TransportProtocol = 17 } }, RuleActionPermit)`；
      - `control` 配置启用（listener port/pid 可随便给一个有效值，block 不走 redirect）；
      - 用 UDP socket `sendto`/`connect` 到 `1.2.3.4:50003`，捕获异常类型与 `SocketErrorCode`；
      - 读 stats 的 `blocked` / `deferred_blocks`（classify 仍早退 UDP，blocked 可能不涨，
        只记录事实）；
      - 再跑一条 TCP block 对照（`protocol=6`），确认 auth filter 路径本身可用。
- [ ] 记录：UDP `send` 是否失败、错误码、listener 是否收到、`blocked` 计数。
- [ ] 结论：auth filter 能否作为 UDP block 的实现；若不能，明确写 STOP。
- [ ] 还原临时改动：`git checkout -- src/common/rule_blob.h tools/wfprobe/*`（按实际改动文件）。

## 4. S4：IPv6 / dual-mode（尽力而为）

- [ ] 临时扩展 `tools/wfprobe/UdpSockets.cs`：解析 `sockaddr_in6`，cmsg 同时接受
      `IPPROTO_IP(0)` 与 `IPPROTO_IPV6(41)`，listener 支持 `[::]` + DualMode；
      `udp-client --remote [::ffff:1.2.3.4]:443` 或 `[2001:db8::1]:443`。
- [ ] `udp-probe` 配置 IPv6 listener 端口；发一条；看 cmsg 与 `redirected`。
- [ ] 若工具改动过大/无法在 VM 上验证，记录：缺什么、下一批怎么补；不要伪造结论。
- [ ] 还原临时改动。

## 5. 产出 findings

- [ ] 写 `research/spike-udp.md`，每节：命令、观察、结论、对 B1/B2 的影响。
- [ ] 给 B2 的断言建议：
      - U3 的 `udp_classify_calls` 增量上限；
      - context 池断言的等待时间与是否要等卸载；
      - UDP block 的错误码与 `udp_blocked` 语义；
      - IPv6 是否进 B3 的必过项。

## 6. 收尾

- [ ] `git status --short`：除 `.trellis/tasks/10-21-host-udp-spike/` 外无改动。
- [ ] 若临时改了文件，逐个 `git checkout -- <file>` 还原并再次确认。
- [ ] 不 commit；把 findings 路径与结论报告给主会话。
