# Phase 0 探针：最小 callout 与重定向验证

## Goal

在 WinLTSC 上验证三件当前只能靠实测确认的事，并留下可复用的最小驱动骨架。

## Requirements

- 最小 WFP callout 驱动（WDM，`src/driver/`），只挂 `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4`。
- 探针行为：对指定 PID（由 IOCTL 或硬编码）发起的 TCP 连接，改写目的地到本地监听端口，并把原始目的地写入 `localRedirectContext`。
- 最小测试程序：监听 + 用 `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` 读回上下文；另有客户端模式发起连接。
- 产出 `research/phase0-findings.md`，逐条给结论与证据。

## 待验证的三件事

1. **重定向能否落到本地监听端口**——官方文档的分支是「local 地址为 ANY 时指向 loopback，否则指向 local 地址」。要在 VM 上实测这两条分支都成立。
2. **redirect context 能否往返**——用户态读回的原始目的地，与客户端实际目标逐字节一致。
3. **`localRedirectContext` 的所有权**——WFP 是否拷贝、由谁释放。这是本批最高风险点：判断错会 UAF 或泄漏。

## Acceptance Criteria

- [ ] 驱动可编译、可签名（`Get-AuthenticodeSignature` 为 `Valid`）、可 `sc start` 到 RUNNING
- [ ] 重定向真实发生：客户端连 `1.2.3.4:443` 被本地监听端口接住
- [ ] 上下文往返一致：监听端读回的原始目的地 == `1.2.3.4:443`
- [ ] 两条地址分支（ANY / 指定 local 地址）都有实测记录
- [ ] 所有权结论有依据（官方文档 + 200 次连接的池用量观察）
- [ ] `sc stop` / `sc delete` 干净，无蓝屏

## Notes

- 本批**不改** `src/common/`，`redirect_context.h` 只读引用。
- 禁止 `git commit`，提交由主会话做。
