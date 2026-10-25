# 设计 — C# 控制器与中继

## 组件

| 组件 | 职责 |
|---|---|
| `configuration/` | 最小 JSON → `PolicyTable`；格式集中在这一层，将来换格式只改这里 |
| `device/` | IOCTL 客户端。ABI 结构以 `src/driver/probe_ioctl.h` 为准，含布局断言 |
| `control/` | 生命周期：编规则 → 下发 → 起监听 → 告诉驱动端口与 self-PID → 等退出 |
| `relay/` | accept → 读 redirect context/records → 建出站 → 双向转发 |

## IOCTL ABI 的单一来源

`tools/wfprobe` 是测试夹具，它有自己的一套 IOCTL 定义。**产品不能复制一份**——那会形成两套会漂移的 ABI。

做法：产品在 `device/` 里定义 ABI 结构与布局断言；`tools/wfprobe` 是否引用产品工程由实现方判断，但**字节布局只能有一处权威定义**，另一处必须是引用或由一致性测试锁定。

## 监听与地址

绑 `0.0.0.0` 与 `::`，端口可以取 0（系统分配）后再把实际端口通过 IOCTL 告诉驱动。这样避免固定端口冲突。

D6 的实测结论：`0.0.0.0` 监听同时接受 loopback 与网卡地址的连接；`127.0.0.1` 只监听则收不到指向网卡地址的连接。驱动侧按 local 地址是否为 ANY 分支决定重定向目标。

## 中继

MVP 用直连：拿到原始目的地后直接 `connect`，双向 `CopyToAsync` 到结束。

注意自流量闸：中继自己的出站连接必须打上标记（由驱动侧 self-PID 集合排除），否则会形成环。B3 已实测 `gates` 套件覆盖了这条。

## 错误处理

用 `std::expected` 的 C# 对应物——`Result<T>` 或直接抛 `ConfigurationException` 一类具名异常。不要用异常做控制流，但**配置错误必须是异常**（用户要看得到）。

## 待定

配置格式是暂定的。实现时要保证：换格式只动 `configuration/`，`control/` 以上不感知。
