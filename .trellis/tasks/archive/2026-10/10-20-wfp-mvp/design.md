# 设计 — WFP MVP

## 组件

| 组件 | 位置 | 职责 |
|---|---|---|
| `engine` | `src/driver/` | DriverEntry/Unload、callout 注册、设备与 IOCTL 入口 |
| `rule_table` | `src/driver/` | 规则 blob 的原子替换与 rundown 保护的并发读 |
| `blob` / `matcher` | `src/common/` | blob 解码校验与谓词求值，**可移植、Linux 可测** |
| `classify` / `redirect` | `src/driver/` | 取 facts → 决策 → 执行；重定向封装与 context 构造 |
| 契约 | `src/common/` | `redirect_context`、`rule_blob` 两个 ABI 结构 |
| 用户态 | `src/userland/`（C#） | 配置解析、策略编译、IOCTL、监听、中继 |

## 关键设计点

1. **内核侧没有规则语义**。进程字符串在编译期规整成内核实际会比较的形式；CIDR 展开为掩码比较；端口区间排序。内核只做比较。
2. **进程比较形式**：先取 `FWPS_METADATA_FIELD_PROCESS_PATH`（DOS 路径），与编译期规整后的规则串做大小写不敏感比较。
3. **`localRedirectContext` 所有权**：必须实测确认——引擎是否拷贝、由谁释放。判断错误将导致 UAF 或泄漏，这是本批最高风险点。
4. **防循环四道闸**的判据与顺序在 B3 落地，B1 只做最小可用（self-PID + 监听端口排除）。

## 批次拆分

| 批次 | 范围 | 依赖 |
|---|---|---|
| B1 | Phase 0 探针：最小 callout + 测试客户端，验 A3 / A4 / 所有权 | 无 |
| B2 | 可移植核心：契约修订 + blob + matcher + Linux gtest | 无 |
| B3 | 驱动规则表 + IOCTL + 完整 classify | B1, B2 |
| B4 | C# 控制器：配置、编译、IOCTL、监听、中继 | B2 |
| B5 | 端到端联调与验收 A1–A9 | B3, B4 |

B1 与 B2 文件集不相交，可并发。

## 风险

| 风险 | 应对 |
|---|---|
| WFP 注册/注销顺序错误导致 BSOD | 严格按 callout → filter → engine 的逆序注销；B1 先跑通最小闭环 |
| 无内核调试器 | 用 IOCTL 回灌日志到用户态，不依赖 WinDbg |
| `dotnet` 11 RC 在 VM 上缺少组件 | B4 开工前先验证 `dotnet build` 能产出 win-x64 |
| 重定向在 loopback 上被引擎拒绝 | B1 首要验证项，失败则改绑 local 地址 |
