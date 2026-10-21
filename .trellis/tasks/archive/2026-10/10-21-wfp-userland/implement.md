# 执行计划 — 控制器与中继

1. `device/`：IOCTL 客户端 + ABI 结构 + 布局断言；先用 `wfprobe` 验证能读到计数器。
2. `configuration/`：最小 JSON schema → `PolicyTable`；含冲突检查镜像（与驱动 `rule_order_valid` 同语义）。
3. `control/`：编规则 → 下发 → 起监听 → 下发端口/self-PID。
4. `relay/`：accept → 读 context/records → 直连 → 双向转发。
5. 端到端手动跑一遍：一个连接从发起走到回显成功。
6. 门禁：`wf_check`、C# 测试、有效行。

不要碰 `src/driver/`、`src/common/`、`tests/host/` 既有文件。
