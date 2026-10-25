# WFP MVP 端到端跑通

## Goal

在 WinLTSC 虚拟机上跑通 WinForward.Kernel 的最小可用闭环：C++ WFP callout 驱动按规则把 host TCP 连接重定向到本地监听端口，C# 用户态取回原始目的地并完成中继，规则由用户态编译后经 IOCTL 下发。

架构决策见 [`.trellis/spec/backend/wfp-architecture.md`](../../spec/backend/wfp-architecture.md)（D1–D9），编码规范见 [`cpp-conventions.md`](../../spec/backend/cpp-conventions.md)。

## Requirements

范围（v1）：**仅 host 域、仅 TCP**。

- 驱动在主战场 `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4`（V6 同构）注册 callout，每连接一次 classify。
- 内核求值策略：放行（`PERMIT`）/ 拦阻（`BLOCK`）/ 重定向（改写目的地 + `localRedirectContext`）。
- 规则表由用户态编译成连续 blob，经 IOCTL 原子替换；驱动侧只解码执行，不做语义判断。
- 重定向目的地址按文档分支：local 地址为 ANY 时指向 loopback，否则指向 local 地址。
- 用户态为 C#：配置解析、规则编译、IOCTL 下发、监听、中继。
- 防循环：self-PID 集合、监听端口排除、`FwpsQueryConnectionRedirectState`、`FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED`。

非目标（本轮明确不做）：

- UDP；forwarded 域（v1 不做，见 D7 与本轮裁定）
- 性能达标（kill criteria 属 Phase 0 / 后续，MVP 只要求能跑通且不退化）
- GUI、安装包、正式签名流程

## Acceptance Criteria

在 WinLTSC 上逐条可复现：

- [ ] A1 驱动签名 `Valid`，`sc start` 后 `STATE: RUNNING`，callout 与 filter 注册成功
- [ ] A2 用户态经 IOCTL 下发规则后，驱动回读的规则数与内容与下发一致
- [ ] A3 测试客户端命中 proxy 规则发起 TCP 连接，连接被重定向到本地监听端口
- [ ] A4 监听端用 `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` 读回的原始目的地，与客户端实际目标一致
- [ ] A5 中继数据正确：1 MiB 回显数据校验和一致
- [ ] A6 未命中规则的连接不被重定向，直连成功
- [ ] A7 命中 block 规则时客户端收到 RST
- [ ] A8 `src/common` 的 blob 与 matcher 在 Linux 上 gtest 全绿
- [ ] A9 连续 load/unload 10 次无蓝屏、无句柄/内存泄漏

## Notes

- 台账：Trellis 父任务 + 批次子任务。
- 提交由主会话执行；子代理禁止 `git commit`。
