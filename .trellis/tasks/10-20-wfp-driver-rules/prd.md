# 驱动规则表与 IOCTL

## Goal

把 Phase 0 探针的硬编码行为换成真正的规则表：blob 经 IOCTL 下发，classify 用 `src/common` 的 matcher 求值，补 V6，补齐防循环与计数器。

## Requirements

- 规则表：从用户态接收 `rule_blob`，校验后原子替换；classify 用 rundown 保护读取旧表。
- classify：命中 `redirect` 才重定向（redirect 失败 = 直连，该层不执行拦截）、`block` 由 AUTH_CONNECT 的 `FWP_ACTION_BLOCK` filter 执行、`permit` / 未命中按 `fallback_action` 放行。
- **V6 callout**：同时注册 `ALE_CONNECT_REDIRECT_V6`。只挂 V4 会漏掉 dual-mode socket（.NET 与浏览器默认），见 spec §7.5。
- 防循环四道闸：self-PID 集合、监听端口排除、`FwpsQueryConnectionRedirectState0`、`FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED`。
- 计数器：classify 次数、各动作次数、重定向次数，经 IOCTL 回读。
- 设备安全：`IoCreateDeviceSecure` + 严格 SDDL 保持不变，不因新增 IOCTL 放宽。

## Acceptance Criteria

- [ ] A1 用户态下发 blob 后，驱动回读的规则数、兜底动作与下发一致
- [ ] A2 非法 blob（魔法错、版本错、截断、偏移越界）被整体拒绝，且原规则表保持不变
- [ ] A3 三条规则（permit / block / redirect）分别命中并产生正确行为
- [ ] A4 未命中时按 `fallback_action` 处理
- [ ] A5 **dual-mode IPv6 socket 的连接能被捕获并重定向**（.NET `TcpClient.Connect(string,int)` 实测）
- [ ] A6 规则表原子替换期间无崩溃、无未定义行为（连续替换 100 次）
- [ ] A7 四道防循环闸各自有负向验证（拆掉任意一道会形成环）
- [ ] A8 计数器与 classify 实际行为一致
- [ ] A9 load/unload 与规则替换后卸载均干净，无蓝屏

## Notes

- 依赖 B1（驱动骨架、所有权与 acquire/apply 结论）与 B2（blob 格式与 matcher，commit `b4bbae2`）。
- 禁止 `git commit`，提交由主会话做。
