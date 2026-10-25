# Forwarded IPv6 数据面 E2E 验收

## Goal

把 forwarded IPv6 从“代码链路已实现、只验证过分片闸”推进到 native IPv6 的 TCP/UDP
happy path、同入口多 L 与 Internal/External 两入口都可复现地 E2E 通过，并接入 Linux
门禁，为 D7 的 IPv6 多入口采用条件提供证据。

## Background

Forwarded 的 IPv6 代码链路已经存在：规则族 0/4/6 与 /128 网段、IPv6 扩展头解析、
TCP/UDP 伪头校验和、DNAT/SNAT 的 16 字节地址改写、分片跟随表（含 IPv6 fragment ID）、
listener 的 v4/v6 绑定、redirect cmsg 的 `sockaddr_in6`、SOCKS5 `atyp=4`。

当前 E2E 证据只覆盖分片闸：

- IPv6 1280 MTU 9 片全部 block（`final3/final4/final5-frag6.log`）；
- IPv6 permit-first 9/9/0（`final3-ipv6-permit-first.log`）；
- IPv6 host 同 remote 不同端口分片按原路径重组、计数 0
  （`final3-ipv6-host-same-remote.log`）。

这些验证的是解析 + 规则匹配 + 分片 fail-closed，不是 DNAT → listener → 中继 →
回程 SNAT 的 happy path。B5 验收和 `src/driver/README.md` 仍写着“native IPv6 UDP
未在入口网卡做 E2E；代码路径已实现但不宣称已支持”，B6b 也把“IPv6 多入口/多 L
未验证”列为已知边界。

## Requirements

1. **夹具支持 v6**：`forwarded-tcp` / `forwarded-udp` 能按地址族选择 listener、
   处理链路本地 scope，并在 `--no-alias` 下使用预先配置的地址；Linux 客户端能发送
   并校验 v6 流量。所有新增地址与防火墙规则由 prepare/cleanup 脚本管理。
2. **TCP v6 core（直连）**：外部 v6 客户端命中 redirect 规则 → DNAT 到 v6 L listener
   → 中继到 v4 backend → 回程 SNAT 恢复原始 v6 目的；双向 64 KiB 字节一致，
   redirect 表记录正确的原始 server 地址/端口，计数恒等式通过。原始目的地址必须与 L
   不同，确保回程 SNAT 写错会失败。
3. **TCP v6 upstreams**：两条 redirect 规则（40250/40251）分别映射 upstream 0/1；
   假 SOCKS5 上游各收到一次 `atyp=4` CONNECT，两个会话双向有数据。
4. **UDP v6 core（直连）**：多个 datagram 经 DNAT → listener → 回程 SNAT；客户端
   校验载荷、数量、SHA 与回程源地址等于原始目的；原始目的上的 decoy 不收到数据。
5. **UDP v6 SOCKS5**：两个目的各建立一次 UDP ASSOCIATE，`atyp=4` 目的正确，
   回程载荷与源地址通过客户端校验。
6. **TCP v6 multi**：同一 Internal 入口两个 L，以及 Internal + External 两入口；
   并发 4 条连接，回程按 L 还原，两个跨入口目的和同入口两个目的都正确，
   `redirect_multi_l >= 1`、`return_ambiguous=0`、`no_ingress=0`。
7. **Linux 门禁**：`forwarded_gate.py` 新增 `--include-v6`，跑上述 v6 阶段；默认
   v4 门禁行为不变。prepare/cleanup 在 v6 开关下增加并删除 Windows 侧地址与规则；
   `run.log` 记录 v6 地址清单和清理结果。禁止用 `--no-verify` 作为证据。
8. **文档与证据**：通过后更新 `src/driver/README.md`、`src/userland/README.md`、
   `.trellis/spec/backend/wfp-architecture.md` 的 IPv6 状态；任务 `verification/`
   保存命令、完整日志、驱动 SHA-256、环境改动与恢复记录。

## Out of Scope

- `WfpController` 的 v6 L 自动解析与配置字段。当前自动解析只找 IPv4，且
  `ForwardedListenerAddress` 没有 JSON 配置入口；控制器属于脚手架，v6 L 选择留给
  WinForward.Runtime/Configuration 集成时处理。本任务只通过 wfprobe 直接下发 ingress
  验证数据面。
- UDP 多 L / 多入口 v6（v4 也未单独 E2E，保持既有边界）。
- IPv6 热替换、逐片 raw 构造、atomic fragment E2E、性能与长稳。
- 驱动数据面语义改动。若 E2E 暴露真实缺陷，修复必须单列并补 host 单测，ABI 仍为 v8。

## Constraints

- v6 地址采用链路本地；原始目的 target 与 L 必须是不同地址，回程 SNAT 才可观察。
  Internal 使用现有主链路本地 L，另加第二 L 与 target；External 使用现有链路本地 L
  并另加 target。Linux 侧只使用 eth0/eth1 现有链路本地地址，不要求 root 改地址。
- Windows 侧地址、防火墙规则必须由 prepare 脚本添加、`-Remove` 删除；地址进入
  `Preferred` 后才开始测试。当前 Internal 的 `ms_tcpip6` 必须为启用状态，脚本发现
  禁用时明确失败，不静默修改。
- 正向用例必须校验实际内容/字节数与回程源地址，不允许只凭计数器 PASS。
- v4 门禁与既有测试必须保持通过；v6 为 `--include-v6` 显式开关。
- 证据落到任务 `verification/`：命令、日志、驱动 SHA-256、环境改动与恢复记录。

## Acceptance Criteria

- [ ] `forwarded-tcp --phase core` v6：64 KiB 双向一致，原始目的 ≠ L，redirect 表
      记录 target 地址/端口，字节数/SHA/ACK 校验通过，计数恒等式通过。
- [ ] `forwarded-tcp --phase upstreams` v6：两个端口分别命中 upstream 0/1，两个假
      SOCKS5 各一次 `atyp=4` CONNECT，两个会话双向有数据。
- [ ] `forwarded-udp --phase core` v6：全部 datagram 到达 listener 并回程，客户端
      看到源地址/端口 = target，decoy 无数据，计数恒等式通过。
- [ ] `forwarded-udp --phase socks5` v6：两个目的各一次 UDP ASSOCIATE，回程载荷与
      源地址校验通过。
- [ ] `forwarded-tcp --phase multi` v6：同入口两 L + Internal/External 两入口共 4 条
      连接全部双向一致，`redirect_multi_l >= 1`、`return_ambiguous=0`、`no_ingress=0`。
- [ ] `forwarded_gate.py --include-v6` 全绿；不带该开关时原有 v4 阶段全绿。
- [ ] 环境恢复：新增 v6 地址、防火墙规则、别名、listener 全部清理，无残留。
- [ ] `verification.md` 记录结果；通过后更新 README/spec 的 IPv6 状态；未通过则写明
      阻断点与剩余边界。

## Technical Notes

- 环境：WinLTSC Internal（ifIndex=5）现有链路本地
  `fe80::8ca8:508a:7f89:be57`；External（14）有链路本地与 ULA
  `fd00:1234:5678:1::2`。Linux eth0/eth1 均有链路本地，eth1 另有 ULA。
  计划新增 Internal target `fe80::99`、Internal 第二 L `fe80::98`、External target
  `fe80::199`；Windows 侧已验证链路本地地址可添加并删除。
- 控制器：`WfpController.ResolveForwardedListenerPlan` 自动解析只找 IPv4；
  `ForwardedListenerAddress` 只是 `ControllerSettings` 属性，JSON 配置无字段。
- 夹具：`ForwardedTcpSuite` 的 `OnLinkAlias` 只支持 IPv4，core/upstreams/fail 的
  listener 固定 `IPAddress.Any`，multi alias 也是 IPv4；`ForwardedUdpSuite` 的
  fragment 已支持 v6，但 core 仍用 IPv4 listener/any/decoy/backend。Linux
  `forwarded_udp_client.py` 固定 `AF_INET`，`forwarded_tcp_b6b.py` 固定 `AF_INET`
  且只接受一个 bind 地址。
- 门禁：`tools/linux/forwarded_gate.py` 只跑 v4；`prepare-b6c-forwarded.ps1` 只加
  IPv4 别名与端口放行，没有 v6 地址准备。

## References

- `.trellis/tasks/archive/2026-10/10-23-fwd-b6-hardening/verification.md` §12、§13.3、§15
- `.trellis/tasks/archive/2026-10/10-23-fwd-b5-udp-gateway/verification.md:170`
- `.trellis/spec/backend/wfp-architecture.md` §7.11–§7.16
- `.trellis/spec/backend/forwarded-rule-blob.md`
- `src/driver/probe_forwarded_frame.cpp:107`
- `tools/wfprobe/ForwardedTcpSuite.cs`、`tools/wfprobe/ForwardedUdpSuite.cs`
- `tools/linux/forwarded_gate.py`、`tools/linux/forwarded_gate.md`
