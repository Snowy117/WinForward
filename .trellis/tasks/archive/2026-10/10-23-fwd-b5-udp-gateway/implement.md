# 执行计划：B5 Forwarded UDP 关联与 SOCKS5 UDP 中继

> 先驱动 UDP DNAT/回程 + 用户态直连 UDP echo，再接 SOCKS5 UDP ASSOCIATE；每步 VM 验证。
> 2026-10-23 完成；验证证据见同目录 `verification.md`。

## 0. 读上下文

- B4b 已提交：`probe_forwarded_redirect.*`、`probe_forwarded_frame.*`、`probe_forwarded_path.*`、
  `probe_forwarded_classify.cpp`、`IRedirectContextSource`/`ForwardedRedirectContextSource`、
  `RelayServer`、`ForwardedTcpSuite`。
- Host UDP：`src/userland/WinForward.Userland/UdpProxy/`（`UdpProxyCoordinator`、
  `Socks5UdpTransport`、`UdpResponseReinjector`）、`tools/wfprobe/UdpSuite.cs`。
- B3 硬约束：`.trellis/tasks/archive/2026-10/10-23-fwd-b3-l2-spike/research/spike-l2.md` §6.1。
- 本任务 `prd.md`、`design.md`。

## 1. 驱动

- [x] UDP 分支：命中 redirect 规则 + 协议 17 → 非分片查/建表 → DNAT 到 (L, listener) →
      重算校验和 → 逐 NET_BUFFER 注入 → 原帧 BLOCK。
- [x] 回程：outbound 源端口==listener 的 UDP → 查表 → SNAT 回 server → 注入入口网卡 →
      原帧 BLOCK；未命中 permit（计 `udp_return_miss`，同时计入 `permitted`）。
- [x] 关联表：首个 datagram 建表、60s 超时、容量 4096、generation、client MAC。
- [x] 失败：无 config/无 L/表满/任意分片/注入失败 → 丢包 + 计数；分片在规则匹配前统一 fail closed。
- [x] 查询 IOCTL 复用 `probe_forwarded_redirect`（protocol=17）。
- [x] stats 追加 UDP 计数；C# 镜像/布局/ABI 测试同步；ABI 升到 6。
- [x] 防环 `FwpsQueryPacketInjectionState0`。

## 2. 用户态

- [x] UDP listener（与 TCP listener 同端口号或独立；先复用同端口），`ReceiveFrom`/`recvmsg`
      拿客户端源。
- [x] `IRedirectContextSource.TryReadUdp`：先查 forwarded 表，再走 host UDP。
- [x] 按 upstream_id 从 `ForwardedUpstreams` 选 SOCKS5；本批自包含实现 `Socks5UdpCodec` /
      `Socks5UdpAssociation` / `ForwardedUdpRelay`（临时偏差，B6 评估薄适配/接回 WinForward.Runtime）；
      关联空闲 30s。
- [x] 回程：同一 listener socket `sendto` 客户端；驱动 SNAT。
- [x] `WfpController`：绑 UDP listener → config → blob → 回读 → enable；退出先 disabled/清表，
      再停 relay/空 blob。
- [x] 失败关闭：查表失败/上游失败丢包，不直连。

## 3. 工具/E2E

- [x] `wfprobe forwarded-udp` 套件：core / socks5 / fail / **fragment** / **capacity** / **reject**；
      core 加原始目的地 decoy 证明原帧 block。
- [x] Linux E2E：UDP echo 40250；fake SOCKS5 UDP 40500；suite 自动加/删 on-link 别名；测试后清理。
- [x] host UDP 回归：`wfprobe udp-rules`；forwarded-tcp core 回归。
- [ ] gate.ps1：forwarded-udp 需要外部 Linux 客户端，本批不塞进 `gate.ps1`；B6 做独立 Linux 门禁。
- [x] 产品 E2E：Controller + fake SOCKS5 UDP，产品日志 `back=5`；直接打 listener 的 query-miss
      计 drop。

## 4. 文档/门禁

- [x] 驱动 README：forwarded UDP 数据面、关联表、回程、**全部分片 fail closed**、失败语义、
      单入口边界、容量测试构建说明。
- [x] spec：`wfp-architecture.md` §7.14；`forwarded-rule-blob.md` UDP 映射；ABI v6。
- [x] Linux `check.sh --full` 8 PASS；C# 226/226；PREfast 0；gate 复跑 PASS（首次失败根因见
      `verification.md` §4：PREfast C6387，已修）。
- [x] 报告：改动、E2E 证据、失败/未覆盖、风险（`verification.md`）。

## 第三轮复核（2026-10-23）

- [x] 0/10 定位：检查者 suite 的 `in=24` 是 30s 背景帧，客户端 datagram 未到达 WFP；用检查者自己的
      `/tmp/b5-udp-client.py` 在手工 Preferred + `--no-alias` 下复跑通过。详见 `repro-round3.md`。
- [x] `OnLinkAlias`：轮询可 bind（Preferred）最多 15s，`--no-alias` 也校验；失败返回 7；lease +
      10 分钟清理 watchdog；正常退出删除租约/地址。
- [x] `verify-gates`：ABI 变异 6→7；23/23 PASS。
- [x] 分片：3×4000B 实测 9 片全部 block；早退全局范围已写入风险（B6 收窄）。
- [x] 回程部分注入、exit 顺序、U7 decoy 绑定失败即 FAIL。
- [x] 产品 E2E 3/3、`back=3`，退出顺序 disabled → 停 relay → 空 blob。
- [x] 最终 gate `GATE-RESULT=PASS`（PREfast 0、userland-e2e 48、vm-check 78）；此前两次 host UDP
      U9/U11 flake 已记录，最终重跑全绿。
## 提交前收尾（2026-10-23）

- [x] TCP client payload 统一为 `index*31+7`；`forwarded-tcp --phase core --bytes 2048` 复跑 PASS，
      客户端 `sha=7c7272c9... ok=1`，suite verify/stats 全 PASS（`verification/tcp-core-*`）。
- [x] 产品 E2E 客户端 verify 已保存：10/10、`back=10`、`source_ok=1 ok=1`；fake 上游 1 次 ASSOCIATE
      + 10 条回显（`verification/b5-controller-udp-final.log`、`product-client.out`、`fake-socks5-product.log`）。
- [x] gate 时序：`gate-b5-final4.log` 对应 `HEAD=e9e7e111...` 的 B5 worktree；gate 之后只改测试脚本/
      注释/证据，未触达 gate 路径，未重跑完整 gate（说明在 `verification/README.md`）。
- [x] B6 记录：U7 decoy 原帧 permit、raw 9 片、reject 产品级、acked、capacity 8/4096、host UDP flake
      （`verification.md` §7.1）。


## 回滚点

- 驱动安装前保留 `C:\workspace\rollback\wfprobe-abi5-b5.sys` 与 `wfprobe-abi5.sys`。
- 表满故障注入用 capacity=8 测试构建，测完已恢复 4096 并重新签名/安装；不把临时容量留进产品。
- 失败时 disabled/空表；本次清理后 `rules=0 fallback=permit config=disabled`，临时别名/防火墙规则删除。
