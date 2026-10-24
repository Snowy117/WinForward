# WinForward.Userland —— 用户态策略编译与控制器

两件事：把有序规则表编译成 `src/common/rule_blob.h` 描述的内核规则 blob，并用**差分测试**锁住
跨语言 ABI；以及把编译结果送进驱动、接住被重定向的连接并中继。

blob 格式一旦对不上，症状是规则静默不生效而不是报错，所以两侧共享的测试是这一层的
主要产出，不是附属品。

## 工程结构

| 路径 | 内容 |
|---|---|
| `WinForward.Userland/Policy/` | 规则模型、编译期校验、blob 编译器、ABI 镜像结构 |
| `WinForward.Userland/Configuration/` | 暂定 JSON 配置 → `ControllerConfiguration` / `PolicyTable`（**唯一**的格式感知层） |
| `WinForward.Userland/Device/` | 控制面 IOCTL：ABI 结构镜像、布局断言、设备客户端 |
| `WinForward.Userland/Control/` | 产品生命周期：下发规则 → 起监听 → 告诉驱动端口与 self-PID → 退出 |
| `WinForward.Userland/Relay/` | accept → 读回原始目的地 → 建出站（直连或经 SOCKS5）→ 双向转发 |
| `WinForward.Controller/` | 控制台入口：`run` / `check` / `state` / `disable` / `echo` / `client` |
| `WinForward.Userland.Tests/` | 零依赖测试驱动、布局断言、ABI 一致性、差分用例表与夹具生成 |
| `Directory.Build.props` | 公共属性：`net10.0`、nullable、warnings-as-errors |

测试工程刻意不引 xunit：仓库的构建面只有 nix devShell 与 Windows VM，两者都不保证能访问
NuGet。入口是 `dotnet run` 的 `test` 子命令，退出码即门禁结果。

C# 工具链由 devShell 提供（`flake.nix` 的 `dotnet-sdk_10`，目标框架 `net10.0`），不依赖宿主的
全局 dotnet。

## 配置（暂定 JSON）

格式是暂定的，**只有 `Configuration/` 下的 `PolicyConfigurationDocument`（主文件与 `.Forwarded.cs` 分部）认识它**；
换格式时只动这个类型，`Relay/` 只见 `PolicyTable`，`Control/` 还接收 forwarded 表与上游表。

```json
{
  "fallbackAction": "permit",
  "socks5": { "host": "127.0.0.1", "port": 1080 },
  "rules": [
    {
      "name": "browser",
      "action": "redirect",
      "protocol": ["tcp", "udp"],
      "family": ["ipv4", "ipv6"],
      "remotePorts": ["80", "443", "8000-8100"],
      "remoteNetwork": ["10.0.0.0/8", "192.168.0.0/16"],
      "process": ["browser.exe", "C:\\Tools\\curl.exe", "D:\\Portable\\App\\"]
    }
  ]
}
```

- `fallbackAction` 必填：`permit` / `block` / `redirect`。
- `socks5` 可选：`{ "host": ..., "port": ... }`，**不写就是直连**。`host` 可以是 IP 或域名（由上游解析）。
- 每个选择器都接受单值或数组；单值等价于单元素数组，旧配置不需要改。同一字段内是 OR、
  字段之间是 AND，规则顺序即优先级、首个命中生效。一条配置规则始终只编译成**一条** blob
  规则，事件与计数里的 `rule_index` 就是配置顺序。
- `process` 的三种写法（同一数组里可以混写）：
  - 不含 `/` 或 `\`：文件名，比较进程路径的最后一段；`dns` 不命中 `dns.exe`。
  - 含分隔符且解析为文件：精确路径，命中该文件本身，以及（对齐 WinForward 语义）位于它之下
    的路径；同名文件在别的目录不命中。
  - 含分隔符且解析为目录：目录前缀，命中直接子文件与任意深度的后代；`C:\Program Files\MyApp`
    不命中 `C:\Program Files\MyApp2\x.exe`。带不带尾斜杠等价。
- 路径型 `process` 在编译期解析成内核实际看到的 `\device\...` 设备路径，要求路径存在（文件或
  目录）；文件名型不做路径解析。两种形态都只折叠 ASCII 大小写，非 ASCII 原样比较。
- 数组不得为空；不限制时请省略字段。`remotePorts` 的 `"*"` 只能单独出现，等价于省略；
  端口 0 拒绝（`*` 或省略才表示任意）；端口按数值排序并合并相邻 / 重叠区间；网段、协议、
  族去重。
- `protocol` / `family` 出现 `any` 视为不限制；与具体值混写时 `any` 优先。
- `protocol` 取 `any` / `tcp` / `udp`；`any` 同时匹配 TCP 与 UDP（相对旧版“any 只匹配 TCP”的
  有意行为变化），需要旧行为时显式写 `tcp`。
- 未知字段直接报错（带 JSON 路径），不静默忽略；空数组、空元素、非字符串元素也带 JSON 路径拒绝。
- host 规则的解析层做结构 + 取值校验（字段名 / 类型 / 数组形状 / 端口语法、端口区间颠倒、端口 0、
  CIDR 语法，错误带 JSON 路径）；跨字段语义（规则顺序冲突、block 表达边界 / 预算、进程路径
  解析与长度、兜底动作矛盾）统一在 `RuleBlobCompiler` 里做，错误消息带规则序号。forwarded
  规则在配置层提前校验 target / adapter / CIDR / 端口，最后由 `ForwardedBlobCompiler` 复核。

## Forwarded 规则与多上游（B2）

`socks5Servers[]` 定义命名上游，`forwarded` 段把入口适配器 / 客户端网段 / 目的网段映射到这些上游：

```json
{
  "fallbackAction": "permit",
  "socks5Servers": [ { "name": "main", "host": "127.0.0.1", "port": 1080 } ],
  "forwarded": {
    "fallbackAction": "block",
    "rules": [
      {
        "name": "vm-tcp",
        "adapterName": "vEthernet (MyVM)",
        "clientCidr": "10.0.0.0/24",
        "remotePorts": "443",
        "action": "redirect",
        "target": "main"
      }
    ]
  }
}
```

- `socks5Servers` 的 `name` 必须非空且唯一（大小写不敏感）；上游 id 按数组顺序取 0..n-1。解析结果保存在
  `ControllerConfiguration.ForwardedUpstreams` 这张 `Id` → endpoint 有序表里；`Upstreams` 名字字典只用于 `target`
  查找，B4 按 id 从有序表取 endpoint，不依赖字典枚举顺序。
- 旧 `socks5`（若有）以 `default` 追加为最后一个上游；`socks5Servers` 里再定义 `default` 会与它冲突。
- `socks5Servers` 只供 forwarded 规则的 `target` 使用；host 中继仍由 `socks5` 段决定直连或单上游。
- `target` 引用的上游名大小写不敏感；`redirect` 必填，`permit` / `block` 禁止出现。
- `adapterId` / `adapterName` 可单独给，也可同时给（必须解析到同一个 id）；带 adapter 的选择器需要
  配置解析时传入 `IAdapterIdResolver`（B4 提供 Windows 实现），没有 resolver 时直接报错。
  `check` / `run` 在 Windows 上会构造 `WindowsAdapterIdResolver` 并在配置期解析；Linux 上没有 resolver，带 adapter 的配置仍按上面的规则报错。
- forwarded 规则没有宿主进程，`process` 字段直接报错；诊断路径是 `forwarded.rules[i].<field>`。
- B2 只编译并报告 forwarded blob（`controller-forwarded count=... upstreams=... blob=...`），
  下发与驱动匹配在 B4。

## 上游 SOCKS5

配置里写了 `socks5` 段，中继的每一条出站连接都先与上游完成一次 no-auth CONNECT，
目标就是驱动读回的**原始目的地**；数据面仍然是纯转发，握手只付一次。地址形态随目标而定：
IPv4（`atyp=1`）、IPv6（`atyp=4`）、域名（`atyp=3`，ASCII，最长 255 字节）。

没有 `socks5` 段时走直连，与没有这个能力时的行为完全一致。

**失败一律上报，不退回直连**：上游不可达、握手超时（沿用 10 秒建连预算）、方法协商被拒、
`rep != 0`、回复版本或地址类型不对，都抛具名 `Socks5Exception`，由会话级 catch 记成
`relay error #<id> ...`，客户端看到的是连接被关，而不是悄悄成功。`relay connected` 行带
`via=direct` / `via=socks5:host:port`，报告里能直接看出这条会话走了哪条路。

`tools/socks5/` 是一个独立的、最小 SOCKS5 服务端（no-auth + CONNECT，带 `connections=` /
`connects=` / 字节计数）和裸 TCP 回显目标，只做端到端测试替身，不进产品工程。
端到端脚本见 `tools/windows/userland-socks5-e2e.ps1`，形态是「客户端 → 驱动重定向 → 控制器 →
SOCKS5（VM loopback）→ 跨机 target」。

## 编译期语义检查

用户态是唯一能给出可诊断信息的时刻——驱动只会整块拒绝。这里的检查和驱动的最后一道闸同语义：

- **规则顺序冲突**（`RuleOrderValidator`，镜像 `rule_table.cpp` 的 `rule_order_valid`）：
  对每条 `block` 规则，若存在更早的非 block 规则在协议掩码、有效地址族、端口区间、网段前缀、
  进程选择器上都可能命中同一条连接，整份规则表拒绝。集合按「任一对元素相交」判断，进程按
  文件名 / 精确路径 / 目录前缀的重叠语义判断；驱动把 block 编译成独立的 AUTH_CONNECT 过滤器、
  不参与「首个命中」顺序，这种配置的语义与作者本意相反。
- **兜底动作矛盾**：`fallbackAction = block` 且表里存在 permit/redirect 规则 → 拒绝。
  驱动会按 TCP(6) / UDP(17) 各创建协议过滤器（IPv4 / IPv6 每层一对），让那些规则命中的
  TCP/UDP 连接变成死规则，而 classify 计数器仍会记「放行」；过滤器只写 6/17，不会误拦 ICMP。
- **进程选择器**：文件名型不得含 `/`、`\`；路径型必须解析成 `\device\` 开头、不以 `\` 结尾的
  设备路径，且要求编译期路径存在（文件或目录）。只折叠 ASCII 大小写，非 ASCII 原样比较；
  控制字符（C0 / DEL / C1）与孤立代理项拒绝。
- **进程路径上限**：UTF-8 ≤ 511 字节（不含 NUL），超限拒绝——超过时内核整条不匹配且不报错。
- **block 表达边界**：block 规则只允许精确文件路径（路径型且非目录）；文件名 / 目录前缀带
  规则序号拒绝。驱动 `allocate_rule_table` 对同形 blob 整块拒绝，避免「classify 记 block、
  auth 层却拦不住」的假 block。
- **block filter 预算**：按 layer × protocol × process × port × network 展开，总数（含
  `fallback=block` 固定占的 4 条）不得超过 `PolicyLimits.MaxBlockFilters = 128`；驱动换表时
  按同一公式再兜底一次。
- 端口 0、端口区间颠倒、族与网段不一致、空数组、未知枚举值等。
- blob 总大小 ≤ `PolicyLimits.RuleBlobMaxSize = 64 KiB`（空池时约 2046 条规则）。

每一条检查都有对应测试，且由 `tools/userland/verify-gates.sh` 做反向验证：拆掉检查必须让测试变红。

## IOCTL ABI 的单一来源

权威定义只有 `src/driver/probe_ioctl.h`（以及 `src/common/redirect_context.h`）。另外两份是镜像：

| 位置 | 角色 |
|---|---|
| `src/driver/probe_ioctl.h` | 权威：`static_assert` 的尺寸/偏移、常量、枚举 |
| `WinForward.Userland/Device/` | 产品镜像 + 运行期布局断言（`DeviceLayout.Verify()`） |
| `tools/wfprobe/Protocol.cs` | 测试夹具（独立程序，不引用产品工程） |

`WinForward.Userland.Tests/AbiConsistencyTests.cs` **读头的文本**：解析结构体字段、`static_assert`、
常量、枚举，再用反射读产品结构的真实布局（`Marshal.OffsetOf` / `Marshal.SizeOf`）、用文本解析读夹具，
逐字段、逐常量、逐枚举比较。只改任意一侧都会让 `abi/*` 测试变红；`tools/userland/verify-gates.sh`
用临时副本改头、改夹具、改产品常量来证明这一点。

**范围**：`abi/*` 锁**控制面 IOCTL**（`probe_ioctl.h` + `redirect_context.h` + 夹具），包括 v5 的
`probe_forwarded_config` / `probe_forwarded_redirect*` 与 `0x80C`–`0x80E`、v7 的 `probe_forwarded_ingress[_config]` 与 `0x80F`/`0x810`，以及 v4 的
`probe_forwarded_info` / `probe_forwarded_stats` 与四个 forwarded IOCTL。Host 规则 blob 另有
`abi/blob` 一组：`BlobAbiConsistencyTests` 读 `src/common/rule_blob.h` 与 `probe_ioctl.h` 的源码
文本，把版本、magic、56 / 32 / 8 / 4 / 20 尺寸、动作值、selector kind、协议 / 族掩码、
511 / 64 KiB / 128 三个上限与 `RuleBlobLayout`、`PolicyLimits`、`tools/wfprobe/Protocol.cs`
三处逐常量互锁；行为一致性仍由 `tests/host` 的跨语言差分 ctest 锁（C# 产 blob、C++ 用同一份
`try_open_blob` + `match` 消费）。差分发现不了「两侧一致地错」，所以 `LayoutTests` 锁字段偏移，
`matcher` / 归一化用例锁单侧语义。


## 产品进程

`WinForward.Controller run --config <文件>` 的顺序：

1. 编译配置（含上面所有编译期检查）；配置非法时在打开设备前以退出码 2 失败，不会碰驱动；
2. 打开 `\\.\WfProbe`（设备 DACL 只允许 SYSTEM/Administrators），校验 ABI 版本；
3. 先把 `enabled` 置 0（清掉上一次异常退出可能留下的死端口配置）；
4. 解析 forwarded 规则里出现的 ingress → 本机 L（当前每个 ifindex 一个 L；显式 ForwardedListenerAddress
   可覆盖），用 `ListenerSet` 在这些 L 上绑定同一个端口（每 L 一个 TCP/UDP socket，v6 显式
   `DualMode = false`）；没有 forwarded 段时退回 `0.0.0.0` / `[::]` 通配绑定；

5. 下发规则 blob，回读规则数/兜底动作/blob 长度并逐项核对；再下发 forwarded 表：配置里有
   `forwarded` 段就用编译出的 blob，没有就下发一张空 permit 表（清掉上一次异常退出留下的规则），
   两种情况都回读 `installed` / 规则数 / 兜底动作 / blob 长度 / generation；
6. 下发 host 配置：`listener_process_id` = 本进程、`listener_port` = P、self-PID 集合 = 本进程；
   有 forwarded 段时按 ingress 下发 `probe_forwarded_ingress_config`：先 `enabled=0` 清 redirect 表，
   再 `enabled=1` 指向每个入口的 L（当前每个 ifindex 一个 L；L 解析失败则回滚为 disabled）；

7. 接受连接 → 依次尝试 forwarded redirect 表与 `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` 读回原始
   目的地（forwarded 命中时按 `upstream_id` 从 `ForwardedUpstreams` 选 SOCKS5）→
   带 `SIO_QUERY_WFP_CONNECTION_REDIRECT_RECORDS`（有的话）建出站（直连，或按所选上游 CONNECT）→
   双向转发；
8. 退出时先把 host `enabled` 置 0，再把 forwarded config 置 disabled（清 redirect 表）并换成空
   permit 表，然后停监听、等会话收尾，最后报告字节数。注意：异常退出 / 进程被杀时不保证恢复
   forwarded config / 表——那时只能靠运维脚本
   或 `wfprobe forwarded-rules --restore-only` 兜底。

中继的出站连接默认带上原连接的 redirect records，驱动因此用**闸 2（redirect state）**放行；
`run --no-redirect-records` 时不带 records，靠 **闸 4（self-PID）** 放行。E2E 两轮分别覆盖这两条路径：
第一轮 `permitted_redirect_state +2`、`permitted_self_process +0`；第二轮（`--no-redirect-records`）
`permitted_self_process +1`、`permitted_redirect_state +0`。拆掉任一道闸都会让 E2E 变红。
读不到 redirect context 的连接直接拒绝（它不是被重定向来的，没有原始目的地可转发）。

会话收尾：一个方向正常 EOF 时只做半关闭，另一方向继续；任一方向异常结束（RST、发送失败、控制器停止）
会取消兄弟泵，避免会话任务与 socket 留到进程退出；半关闭后对端若一直不关，30 秒后强制收尾。

已知限制：MVP 不限制并发会话数（`_sessions` 无界），也没有空闲超时；生产部署需要加连接上限。

上游 SOCKS5 的验证分两层：`Socks5Tests` 用 loopback 假上游逐字节核对 IPv4 / IPv6 / 域名三种
CONNECT 请求与每条失败路径（含「上游失败不回退直连」）；`tools/windows/userland-socks5-e2e.ps1`
在 VM 上跑真实链路，客户端 1 MiB 数据自带 SHA-256 对照，替身报 `connections=` / `connects=`。


## 构建与测试

一条命令（生成夹具 → 构建 → 全部 ctest，任一失败即失败）：

```bash
cmake -S . -B build -G Ninja
cmake --build build --target wf_check
```

分开跑也可以，但**顺序不能反**：

```bash
# C# 侧：单元测试 + 布局断言 + 差分夹具生成
dotnet build src/userland/WinForward.Userland.slnx
dotnet run --project src/userland/WinForward.Userland.Tests -- test
dotnet run --project src/userland/WinForward.Userland.Tests -- abi      # 只跑 ABI 一致性
dotnet run --project src/userland/WinForward.Userland.Tests -- vm-check # 需要驱动，见下

# C++ 侧：读取夹具，逐条断言决策一致
cmake --build build
ctest --test-dir build --output-on-failure
```

`vm-check` 只在 Windows VM 上跑（驱动已加载时）：ABI 握手、用真实 IOCTL 的缓冲区长度验证结构尺寸、
进程路径 511/512 边界、句柄法解析（当前进程文件与目录）、以及「同一份规则表，C# 冲突检查与驱动
`rule_order_valid` 结论一致」的差分矩阵（含 `tools` vs `tools2`、目录遮蔽后代、多端口相交 /
不相交、混合族与精确路径 / 目录选择器命中）。

两侧的夹具目录必须一致。C# 侧默认写 `<仓库根>/build/diff-fixtures`；C++ 侧默认读
`${CMAKE_BINARY_DIR}/diff-fixtures`，用标准构建目录 `build` 时两者相同。用环境变量
`WF_DIFF_FIXTURE_DIR` 可以同时改掉两侧的默认目录。

C++ 侧读不到夹具会直接失败（并打印生成命令），不会静默跳过；只改 C# 期望却不重跑生成时，
`ctest` 读的是旧夹具 —— 这正是 `wf_check` 存在的理由，它先把夹具重新生成一遍再跑全套。

## 有效行

单文件有效行（去掉空行与整行注释）不超过 500，检查脚本会真的失败：

```bash
python3 src/userland/scripts/check-effective-lines.py
```

## 与内核的边界

Host 规则 blob 当前是 **v2**：56 字节头 + 32 字节规则 + 8 字节 selector + 4 字节端口 +
20 字节网段，段序为 `header | rules | process_pool | selector_pool | network_pool | port_pool`。
v1 不再读取，控制器 / 驱动 / `wfprobe` 必须同批升级；控制面 IOCTL 仍是 v7。

- 所有取舍在编译期做完：路径型进程选择器经句柄法（`WindowsProcessPathNormalizer` +
  `WindowsDevicePath`）解析成 `ALE_APP_ID` 同形的设备路径，折叠 ASCII 小写；进程串按 NUL 结尾
  去重；selector / 网段 / 端口按规则顺序连续追加，规则只持有池下标与数量；一条配置规则对应
  一条 blob 规则。
- 内核侧 `try_open_blob` 做全部字节级校验（段边界 / 重叠 / 对齐、UTF-8 与控制字符、511 字节、
  selector 形态与 `\device\` 前缀、端口区间、单族与网段一致等），`match` 按数组顺序找首个
  命中，不解释用户意图；block 表达边界、filter 预算与顺序冲突在驱动 `allocate_rule_table`
  再拒绝一次。
- 兜底动作写在 blob 头部，与规则表一起原子替换；
- 兜底 `block` 会按 6/17 生成协议过滤器（IPv4 / IPv6 各一对），因此「兜底 block + 非 block 规则」
  是矛盾配置，在编译期拒绝。
- 本仓库的用户态 `Relay/` 仍是 TCP 中继；UDP 的 listener / 中继在 WinForward 侧实现，
  UDP 重定向的接住与回包不在当前控制器范围内。


## B6c 门禁与边界（2026-10-23）

- 独立 Linux 门禁 `tools/linux/forwarded_gate.py` 走产品 listener/reject harness；Windows-only
  `gate.ps1` 不含需要外部 Linux 客户端的 forwarded 数据面。
- 产品控制器每个 ifindex 只解析一个 L；UDP 多 L 并发多目的、UDP 热替换、IPv6 多入口未 E2E；
  LWF 注入可见性未验证。统一边界见任务 `verification.md` §15。
- `StateCommand` 输出 `forwarded` / `forwarded-ingress` / `forwarded-stat`（ABI v7）。
