# 技术设计：B4a 驱动 L2 骨架与 Forwarded pass/block

## 1. 驱动结构

新增 `src/driver/probe_forwarded.cpp` / `probe_forwarded.h`，与现有 ALE 路径并列：

- `probe_forwarded_state`：forwarded 规则表（`wf_forwarded_blob` 副本 + generation +
  refcount）、计数、callout id、injection handle（B4b 用，B4a 可先创建但不注入）。
- `probe_forwarded_register_callouts()` / `probe_forwarded_unregister_callouts()`：
  - `FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE` + `FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE`
    （B3 实测 inbound layer 58、outbound 59；代码用常量，不写死数字）。
  - 注册/注销按现有 `FwpsCalloutUnregisterById0` 重试规则。
- classify：
  1. `FwpsQueryPacketInjectionState0` 为 self/previously-injected → permit（防环）。
  2. 从 `layerData`（`NET_BUFFER_LIST*`）解析以太网头（含 0x8100/0x88A8 VLAN）、
     IPv4/IPv6、TCP/UDP；失败 → permit（不误伤）。
  3. 取 `FWPS_FIELD_..._INTERFACE_INDEX` 作为入口 ifIndex；出向同。
  4. 调用 `match_forwarded`；命中 action=permit → `FWP_ACTION_PERMIT`；
     block → `FWP_ACTION_BLOCK`；未命中 → fallback（permit/block）。
  5. 计数：permitted/blocked/fallback/malformed/matched；按 adapter/rule 可选。
- 不修改帧（B4a 只读）；不注入。

与 host 路径的边界：

- host 的 ALE callout、`rule_table`、`probe_config`、`probe_stats` 不动；
- forwarded 规则表独立（`wf_forwarded_blob`），generation 独立；
- 驱动入口先注册 host，再注册 forwarded；卸载逆序。

## 2. IOCTL / ABI v4

新增：

```c
struct probe_forwarded_info {          // 实际实现：32 字节
    uint32_t abi_version;              // 偏移 0
    uint32_t generation;               // 每次成功替换 +1
    uint32_t installed;                // 1 = 当前有表
    uint32_t rule_count;
    uint32_t fallback_action;
    uint32_t total_size;
    uint32_t network_pool_count;
    uint32_t reserved0;
};

struct probe_forwarded_stats {         // 实际实现：152 字节
    uint64_t classify_calls;
    uint64_t inbound_frames;
    uint64_t outbound_frames;
    uint64_t permitted;
    uint64_t blocked;
    uint64_t matched;
    uint64_t matched_permit;
    uint64_t matched_block;
    uint64_t fallback;
    uint64_t fallback_permit;
    uint64_t fallback_block;
    uint64_t self_injected;
    uint64_t no_table;
    uint64_t malformed;
    uint64_t non_ip;
    uint64_t no_write_right;
    uint64_t rejected_blobs;
    uint64_t tables_replaced;
    uint32_t last_status;
    uint32_t abi_version;
};
```

布局另有两条实现约束：`probe_forwarded_stats` 的第 17/18 个字段是 `rejected_blobs` / `tables_replaced`，
`abi_version` 在偏移 148；info 的 `reserved0` 在偏移 28。C++ `static_assert`、C# `DeviceLayout` 与
`tools/wfprobe/Protocol.cs` 三处逐字段锁定，改动必须同时提升 `probe_abi_version`。
- `ioctl_set_forwarded_blob`：输入 raw `wf_forwarded_blob`（4 字节对齐），驱动
  `try_open_forwarded_blob` 校验后原子替换；非法整块拒绝，旧表不动。
- `ioctl_get_forwarded_info`：回读 abi_version / generation / installed / rule_count /
  fallback_action / total_size / network_pool_count。
- `ioctl_get_forwarded_stats`：回读计数（见上）。
- `ioctl_reset_forwarded_stats`：清零 forwarded 计数；host 的 `ioctl_reset_stats` 语义不变。
- `redirect` 规则整块拒绝（`STATUS_NOT_SUPPORTED`）：B4a 不执行重定向；`CheckCommand` / `RunCommand`
  在打开设备前就拒绝并指出 B4b，避免半启动。
- `probe_abi_version` 从 3 升到 4；设备 README 与 C# 布局断言同步。
- 不新增 `probe_config` 字段；forwarded 不需要 listener/self-PID（B4c 的用户态中继走 host
  的 self-PID 逻辑或 B4b 的 redirect 表）。

## 3. 规则匹配

复用 `src/common/forwarded_blob.h` + `forwarded_matcher.h`：

- `forwarded_connection_view` 的 `ingress_adapter_id` = ifIndex；
- `client_address`/`remote_address` 从帧解析；
- `remote_port` 对 TCP/UDP 生效；ICMP/其它协议不命中端口规则（按 fallback/协议 any 处理）；
- `protocol` 用 6/17/0；非 TCP/UDP 的协议值不匹配具体规则；
- `family` 用 4/6。

匹配顺序按 B1 matcher：adapter → family → protocol → 端口 → remote CIDR → client CIDR；
首个命中。

## 4. 适配器解析

实现 B2 的 `IAdapterIdResolver`：

- 新增 `src/userland/WinForward.Userland/Forwarded/WindowsAdapterIdResolver.cs`（与接口同目录）：
  用 `NetworkInterface.GetAllNetworkInterfaces()`，按 `Id`（stable GUID）或 `Name` 匹配；
  `GetIPProperties().GetIPv4Properties()?.Index` / `GetIPv6Properties()?.Index` 取 ifIndex。
- `TryResolve(stableId, name, out uint id, out string error)`：
  - 两者都给且解析到不同 ifIndex → 错误；
  - 找不到/ifIndex 为 0 → 错误；
  - Linux 上无 Windows 适配器 → 返回错误，由测试 fake 覆盖配置解析。
- `RunCommand`/`CheckCommand` 在 Windows 上传入真实 resolver；Linux 上保持无 resolver
  （带 adapter 的配置会按 B2 的规则报错）。

## 5. wfprobe 与验证

`tools/wfprobe/` 新增 `ForwardedSuite.cs`（命令 `forwarded-rules`）：

- 打开设备 → 校验 ABI v4 → reset forwarded stats → 下发 forwarded blob → 回读 info。
- F1/F2/F4/F5 的出向流量用**固定源端口**（`--source-port`，默认 40310 起）连接外部 peer：
  出向 `remote_port` = 源端口，block 规则因此只命中测试流，不会碰 WinRM 5985/5986。
- 用例：
  - F1 pass：ifIndex + client/remote CIDR + 端口（= 固定源端口）→ matched_permit 增加，连接成功；
  - F2 block：同一端口收窄的 block 规则 → matched_block/blocked 增加，客户端 TCP 超时/失败；
  - F3 fallback permit/block（fallback=block 时先放行 WinRM 端口，用 ping 网关触发未命中帧）；
  - F4 多规则顺序（两个方向各一个固定源端口）；
  - F5 client/remote CIDR 与端口不命中；
  - F6 非法 blob（magic/截断/redirect）拒绝，旧表 generation 不变；
  - F7 IPv6 happy path（`--peer6`；本 VM 默认关闭 Internal IPv6，属环境未验证）；
  - F1i/F2i 入向：`--inbound-wait-ms` 窗口内由外部客户端连接本机监听端口。
- 必需用例（F1/F2/F3/F6）被 SKIP 时套件以非 0 退出并打印 `verdict=SKIPPED`；套件级看门狗在
  进程被杀时把 forwarded 表恢复成空 permit 表。
- VM 门禁：`load-driver.ps1` 构建/安装；`gate.ps1` 增加 `forwarded-rules --only F1,F2,F3,F6`
  步骤；host 既有 `wfprobe rules` / `gates` / `selftest` 仍 PASS。

## 6. 测试与文档

- C++：`forwarded_blob`/`matcher` 已有单测；新增驱动侧 ABI 布局断言（`probe_forwarded.h`）
  与 C# `DeviceLayout`/`ProbeForwarded*` 布局测试。
- C#：`WindowsAdapterIdResolver` 的纯映射逻辑（用注入的接口快照）单测；`WfpDeviceClient`
  的新 IOCTL 方法。
- 文档：`src/driver/README.md` 记录 L2 callout、layer 58/59、NBL 解析、block 语义（不注入
  RST）、防环、B4b/B4c 边界；`wfp-architecture.md` 补 B4 事实（B4a 完成后）。

## 7. 风险

| 风险 | 处理 |
|---|---|
| L2 callout 与现有 ALE/host 冲突 | 独立 callout id/规则表；先只 permit/block，不碰注入；VM 上先跑 host 既有 gate |
| L2 层 block 不生效或客户端行为不同 | B4a 只断言“连接失败/超时”，不承诺 RST；RST 留 B4b |
| MAC 层字段缺失 | 按 B3：自己解析 NBL，VLAN 偏移按 0x8100/0x88A8 |
| 出向 layer 59 | 用常量 `FWPS_LAYER_OUTBOUND_MAC_FRAME_NATIVE`，不写死 59 |
| ifIndex 映射错误 | resolver 只在 Windows 用；配置期拒绝 0/找不到；测试 fake 覆盖 |
| 卸载竞态 | 沿用现有重试；先注销 forwarded 再 host |
| ABI v4 破坏 host | 只新增 IOCTL/结构，不改现有 `probe_config`/`probe_stats`；C# 布局测试 |
