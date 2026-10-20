# 设计 — 驱动规则表与 IOCTL

## 结构

沿用 Probe 的骨架，替换"硬编码探针"为"规则驱动"：

| 模块 | 变化 |
|---|---|
| `rule_table` | 新增。持有 blob 的池内存副本 + rundown 保护；`replace(blob) -> NTSTATUS` |
| `classify` | 从"只比 PID"改为"构造 `connection_view` → `match()` → 按 action 执行" |
| `engine` | 新增 V6 callout 注册；IOCTL 增加"下发规则""回读规则""读计数器" |
| `redirect` | 不变（Phase 0 已验证） |

## 规则表并发模型

- 表本身是不可变的 blob 副本，替换时分配新块、校验通过后交换指针，旧块在 rundown 排空后释放。
- classify 在 `DISPATCH_LEVEL` 只持有 rundown 引用，不做阻塞等待。
- 替换失败（非法 blob）时不改动当前表——A2 的判据。

## classify 决策

```
构造 connection_view（进程路径小写化、远端地址、端口、协议、地址族）
match(blob, connection_view)
  matched        → 按 rule.action
  !matched       → 按 blob.fallback_action
```

进程路径取 `FWPS_METADATA_FIELD_PROCESS_PATH`（DOS 路径），小写化后与 blob 里规整过的串比较。小写化在 classify 内做，需控制成本：只在路径长度变化时才拷贝，否则就地比较（大小写不敏感比较，不分配）。

## V4 / V6

两个层共用一份 classify 实现，用 `layerId` 分支取地址与端口字段；`connection_view.family` 由层决定。

## 防循环四道闸

按代价从低到高排列，命中即 early permit：

1. 目标端口等于监听端口；
2. `FWP_CONDITION_FLAG_IS_CONNECTION_REDIRECTED` 已置；
3. `FwpsQueryConnectionRedirectState0` 报告已被本 callout 重定向；
4. 发起进程 PID 在 self-PID 集合内。

## 计数器

无锁累加（`InterlockedIncrement64`），IOCTL 回读快照。字段：`classify_total`、`permit`、`block`、`redirect`、`rejected_blob`。

## 待定

**卸载时注销失败怎么办**。当前探针是"上限 30s，超时后继续卸载"。这与"绝不在未注销成功时卸载"的原则冲突。三个选项（无限重试 / 继续卸载 / 主动 bugcheck）的取舍在 B1 复核结论出来后定，本批沿用最终结论。
