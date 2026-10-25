# C# 控制器与中继

## Goal

把已有的零件串成一个能跑的产品进程：读配置 → 编规则 → 经 IOCTL 下发 → 监听被重定向的连接 → 读回原始目的地 → 中继。

## Requirements

- **配置**：格式**暂定**（用户明确说配置格式以后再定），用一个最小 JSON schema，能表达 host 规则表与兜底动作即可。格式要单独成文件、集中解析，将来换格式时只动这一处。
- **IOCTL 客户端**：下发规则、回读计数器与状态。ABI 以 `src/driver/probe_ioctl.h` 为准。
- **监听**：绑定 `0.0.0.0` 与 `::`（D6 实测：这样同时覆盖 loopback 与网卡地址），把端口告知驱动。
- **重定向上下文**：accept 后用 `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` 读回原始目的地；同时取 `SIO_QUERY_WFP_CONNECTION_REDIRECT_RECORDS` 供出站关联。
- **中继**：按原始目的地建出站连接并双向转发。SOCKS5 可以先不做，直连足够让 MVP 闭环。
- **自流量**：与驱动共用同一份 self-PID 集合语义，避免中继自己的出站连接被再次重定向。
- **冲突检查**：编译期复刻 `rule_order_valid` 的判定，让用户拿到清楚的错误而不是驱动的 `STATUS_INVALID_PARAMETER`。驱动仍是最后一道闸。

## Acceptance Criteria

- [ ] A1 配置文件 → `PolicyTable` → blob → IOCTL 下发，驱动回读的规则数与兜底动作一致
- [ ] A2 非法配置（进程路径 >511 字节、block 规则被更早的 permit 覆盖、端口区间颠倒）在**编译期**报错，错误信息带规则序号
- [ ] A3 监听就绪后，被重定向的连接能被 accept，读回的原始目的地与真实目标一致
- [ ] A4 中继数据双向正确（≥1 MiB 校验和一致）
- [ ] A5 中继自身的出站连接不被再次重定向（自流量闸生效）
- [ ] A6 控制器退出时干净：停止监听、无需卸载驱动即可重启
- [ ] A7 单文件有效行 ≤ 500，命名按 .NET 惯例

## Notes

- 依赖 B4a（规则编译器，commit `d4e91b8`）与 B3（驱动 IOCTL ABI）。
- SOCKS5、UDP、配置热重载都不在本批。
- 禁止 `git commit`。
