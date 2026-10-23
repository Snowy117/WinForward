# 执行计划：B6 Forwarded 收口

> 分阶段：B6a 正确性 → B6b 多入口/热替换/观测 → B6c 门禁/性能/文档/验收。
> 每个阶段独立验证、独立提交；不把测试替身当产品验证。

## 0. 读上下文

- B6 `prd.md`、`design.md`。
- B5 归档：`.trellis/tasks/archive/2026-10/10-23-fwd-b5-udp-gateway/verification.md`、
  `verification/README.md`、`repro-round3.md`。
- B4b/B5 代码：`src/driver/probe_forwarded*`、`Relay/*Udp*`、`ForwardedTcpSuite/UdpSuite`、
  `tools/linux/forwarded_*.py`、`tools/windows/prepare-b5-udp-e2e.ps1`。
- B3 硬约束/成本：`.trellis/tasks/archive/2026-10/10-23-fwd-b3-l2-spike/research/`。

## B6a：正确性收口

- [x] U7 decoy `available=1`：复现为“旧 suite 遗留的恢复看门狗清掉后来者的表 → 无规则命中走
      fallback permit → decoy 收到原帧”；修复为看门狗 generation 所有权、suite `finally` 也只恢复
      自己那一代、窗口校验与 decoy 硬计数。single/no-verify 复跑 PASS。证据 `verification.md` §1。
- [x] 分片 raw/显式 9 片：IPv4 raw 逐片构造；IPv6 用 1280 MTU + PMTUDISC_DONT 的 9 片（限制已
      记录）；精确断言 9/9/0。证据 §2。
- [x] 分片范围：改为**首片完整规则匹配 + (地址, IP ID) 跟随表**，非首片查表；permit-first 负测
      （9/9/0）与 host 同 remote 不同端口正测（重组收到 544B、计数 0）均 PASS。证据 §2.2/§2.3。
- [x] reject 产品级：新增 `tools/windows/forwarded-udp-reject-e2e.ps1`，真实 Controller +
      `ForwardedUdpRelay` + fake `rep=7`；`REJECT-E2E-RESULT=PASS`（relayed=0/back=0/associations=3/
      failures=3/decoy=0）。证据 §3。
- [x] acked 真实 TCP ACK：`TCP_INFO.tcpi_bytes_acked`，客户端/suite 统一 `[payload, payload+1]`。§4。
- [x] stop/UAF：完成例程的 decrement 移到所有 free 之后；10s 排水 + 超时泄漏池 + 服务键诊断；
      故障注入证明完成例程按 4.5s 延迟（2 次），但 stop entry 非零采样受控制通道延迟限制，未完全
      验证；5 次 stop/start 无泄漏无崩溃。证据 §5。
- [x] 混合 NBL：入口逐 NB 5 元组 fail-closed；TCP 回程改为“全成功才 redirected，failed>0 即
      blocked+return_failed+inject_failed”；部分注入失败无法从网络构造，记录为未验证。§6。
- [x] capacity 4096：生产 4096 真满；U9/U11 改为事件归属 + 连续两次 `udp-rules` PASS。§7/§8。
- [x] 文档：`forwarded-rule-blob.md`、`wfp-architecture.md`、`src/driver/README.md` 的“所有 UDP
      分片 block / capacity=8”旧描述已更新；reject 夹具说明已写入 suite。
- [ ] LWF 关闭注入可见性：WinpkFilter `nt_ndisrd` 已启用且全部 E2E 通过；VM 无用户态 API/工具，
      注入副本在 LWF 内的可见性未直接测量（§9），留待有 SDK 工具时补测。

## B6b：多入口/多 L/多目的/热替换/观测

- [x] per-ingress L/listener：ABI v7 新增 `probe_forwarded_ingress[_config]` 与 0x80F/0x810；
      legacy 28 字节 config 等价于通配记录。驱动按 ingress 选 L，未配置入口 fail-closed；
      用户态 ListenerSet 按 L 绑同一端口，WfpController 按规则 ingress 解析并回读。
      实测 Internal(5) + External(14) 两入口与同一入口两个 L。证据 `verification.md` §13。
- [x] 回程 key 含 server：入口/回程精确/回程候选三条索引；L 与 server 进入回程 key，
      候选唯一才放行，歧义 fail-closed。同一客户端端口并发访问两个目的（跨入口与同入口多 L）
      回程不串。§13。
- [x] 热替换 generation：只换 blob 不清表；旧表项 generation/upstream 不变，新表项新一代。
      实测 32→33（复核复跑 57→58），旧连接 upstream 0、新连接 upstream 1。§13。
- [x] StateCommand：输出 `forwarded`、`forwarded-ingress`、`forwarded-stats/stat`（含 v7 计数）。
      `b6b-state.log`。
- [x] 事件/计数器按 ingress/upstream：查询/状态按入口与 upstream 可读；新增
      redirect/udp_return_ambiguous、*_no_ingress、*_multi_l 计数。§13。
- [x] 复核收口：产品路径明确每 ifindex 一个 L（同 ifindex 多 L 仅驱动/夹具）；新增
      `noingress` 正测与“任意 L 查询歧义”正测；控制查询不再污染 data-path ambiguous；
      UDP 混合 NBL miss 独立计数；旧尺寸 IOCTL 由 `vm-check` 实测；防火墙/临时件清理并加
      `prepare-b6b-forwarded.ps1`；hotswap 旧连接换表后继续收发。证据 §13.4。



## B6c：门禁/性能/文档/验收

- [ ] 独立 Linux 门禁：`tools/linux/forwarded_*.py` + `prepare-b5-udp-e2e.ps1`；
      verify 端口、CI 禁止 `--no-verify`。
- [ ] gate.ps1 纳入 forwarded-udp core/fragment/reject（可选部分）。
- [ ] A5 成本：iperf3/内核 sink；同 pps/包大小；记录基线。
- [ ] 文档：AGENTS/wfp-architecture/README/spec；D7 决策记录。
- [ ] 最终：host/forwarded TCP/UDP/gate 全绿；归档 B6/父任务；`update_goal complete`。

## 验证

- Linux：`tools/check.sh --full`、`dotnet run ... -- test`、`verify-gates.sh`。
- VM：B6a/B6b 套件、host `rules`/`udp-rules`/`selftest`/`gates`、`gate.ps1`（时间允许）。
- E2E：Linux 客户端 + prepare 脚本；保留原始日志到 `verification/`。

## 回滚点

- 每个 B6a/B6b 项独立提交；驱动安装前保留签名回滚版本；失败卸载/回滚，不 reset VM。
