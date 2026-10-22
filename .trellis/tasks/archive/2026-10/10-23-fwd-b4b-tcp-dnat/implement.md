# 执行计划：B4b TCP DNAT、redirect 表与回程中继

> 先打通“DNAT → listener → 直连中继 → 回程”，再接 SOCKS5/upstream；每步在 VM 上验证。

## 0. 读上下文

- B4a 已提交：`src/driver/probe_forwarded*.{h,cpp}`、`probe_ioctl.h`（ABI v4）、
  `tools/wfprobe/ForwardedSuite.cs`、`ForwardedBlobBuilder.cs`、`ForwardedRuntimeSupport.cs`、
  `WindowsAdapterIdResolver.cs`、`WfpController`。
- B3 硬约束：`.trellis/tasks/archive/2026-10/10-23-fwd-b3-l2-spike/research/spike-l2.md` §6.1。
- B2 接缝：`ForwardedUpstreams`、`IAdapterIdResolver`、`RedirectDestination`、`RelayServer`、
  `WfpDeviceClient`。
- 本任务 `prd.md`、`design.md`。

## 1. 驱动：config/ABI v5

- [x] `probe_forwarded_config`（enabled/listener_port/L/family）+ set/get IOCTL；
      ABI v5；C++ static_assert + C# 镜像/布局。
- [x] 控制器/工具夹具同步 ABI v5。
- [x] 无 config 时 redirect 规则仍拒绝或 block；文档写清。

## 2. redirect 表

- [x] 入口表：`(ingress, client, server) -> (listener, client_mac, upstream, rule, state)`；
      回程表：`(listener, client) -> entry`。
- [x] SYN 建表；数据命中；FIN/RST closing；超时回收；容量 4096；generation。
- [x] 计数：active/high_water/capacity/no_state/timeout。
- [x] 查询 IOCTL `ioctl_query_forwarded_redirect` + C# 镜像/布局。

## 3. 入口 DNAT / 注入 / 原帧消费

- [x] 命中 redirect 规则 + TCP：查/建表 → 改写 dst 到 (L, listener) → 重算校验和 →
      clone/改写 → `FwpsInjectMacReceiveAsync0` → 原帧 `FWP_ACTION_BLOCK`。
- [x] 非 SYN 无表项、无 L、表满、注入失败 → block + 计数。
- [x] 非 TCP redirect → B4b 先 block；UDP 留 B5。
- [x] 验证：客户端 SYN 到 listener；IOCTL 查到 server/upstream；原帧未继续。

## 4. 回程 SNAT / 注入

- [x] outbound 源端口==listener → 查回程表 → 源 IP/端口改回 server →
      目的 MAC=client_mac、源 MAC=入口帧目的 MAC → 重算校验和 →
      `FwpsInjectMacSendAsync0` 注入入口网卡 → 原帧 BLOCK。
- [x] 未命中表的 listener 包 permit（不能误改）。
- [x] 验证：客户端看到 server 地址/端口；数据一致；半关闭/RST。

## 5. 用户态

- [x] `IRedirectContextSource`：host（SIO_QUERY）/forwarded（新 IOCTL）；`RelayServer`
      依次尝试；都失败拒绝。
- [x] `RedirectDestination` 带 upstream_id/ingress；按 `ForwardedUpstreams` 选 SOCKS5。
- [x] `WfpController`：绑 listener → set config(disabled) → set blob → 回读 →
      enable；退出 enabled=0/清表。
- [x] 失败关闭：查表失败拒绝；上游失败 RST；不直连。

## 6. 工具/E2E

- [x] `wfprobe` 新增 `forwarded-tcp` 套件（或扩展现有）：
      T1 SYN→listener/查表；T2 直连中继字节一致；T3 回程源地址；T4 半关闭/RST；
       T5 无 L/表满/上游失败；T6 原帧 block；T7 两客户端/两 upstream。
- [x] 检查阻断返工：MAC 层一个 NBL 多个 NET_BUFFER 时逐帧查询/注入；完成例程状态计数；
      stop 等待 outstanding 注入；套件 verify 端口断言客户端实收/SHA/ACK（T1–T7 全部复跑）。
- [x] Linux E2E：临时路由 `10.99.0.0/24 via 192.168.100.2`；echo 目标 40250；
      fake SOCKS5（40000–41000）；测试后删除路由。
- [x] VM gate：`gate.ps1` 全量 PASS；forwarded-tcp 由本批手工两段式驱动（含外部 Linux 客户端），纳入自动门禁留 B6。
- [x] host 回归：rules/selftest/gates/forwarded pass/block。

## 7. 文档/门禁

- [x] 驱动 README：forwarded TCP 数据面、配置、表、原帧消费、回程、失败语义、单入口边界。
- [x] spec：`wfp-architecture.md` 补 B4b 事实；`forwarded-rule-blob.md` 的 redirect 说明更新。
- [x] `tools/check.sh --full`、C# 测试、VM gate。
- [x] 报告：改动、E2E 证据、失败/未覆盖、风险。

## 回滚点

- 先做 DNAT/直连中继，再 SOCKS5；每步独立可回退（关闭 forwarded config 回到 pass/block）。
- 驱动安装前保留上一个签名版本；失败卸载/回滚，不 reset VM。
