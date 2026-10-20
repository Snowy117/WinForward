# 实现结论 — 用户态控制器与中继

> 实测平台：WinLTSC（Windows 11 IoT Enterprise LTSC，192.168.100.2）。
> 日期：2026-10-21。代码：`src/userland/`（产品与测试）、`tools/vm/`（门禁脚本）。

## 1. IOCTL ABI 的单一来源

权威定义只有 `src/driver/probe_ioctl.h` 与 `src/common/redirect_context.h`。另外两份是镜像：

| 位置 | 角色 |
|---|---|
| `src/driver/probe_ioctl.h` / `src/common/redirect_context.h` | 权威（`static_assert`、常量、枚举） |
| `WinForward.Userland/Device/` | 产品镜像 + 运行期 `DeviceLayout.Verify()` |
| `tools/wfprobe/Protocol.cs` | 测试夹具（独立程序，不引用产品） |

`WinForward.Userland.Tests/AbiConsistencyTests.cs` 读 C++ 头的**文本**：解析结构体字段、
`static_assert`、常量、IOCTL 功能号与枚举；再用反射读产品结构真实布局（`Marshal.OffsetOf` /
`Marshal.SizeOf` / `Unsafe.SizeOf`）、用文本解析读夹具定义，逐字段/逐常量/逐枚举比较。
`tools/userland/verify-gates.sh` 用临时副本改头、改夹具、改产品常量，确认每处都能让 `-- abi` 变红。

VM 上另有一条更硬的证据：`vm-check` 直接拿真实驱动验 `probe_config` 60 字节被拒 / 64 字节被接受、
`GetStats` 恰好返回 232 字节、短 8 字节被拒——这是运行中的内核在做长度判据，不是 `static_assert`。

## 2. 编译期语义检查（与驱动同语义）

- **规则顺序冲突**：`RuleOrderValidator` 镜像 `rule_table.cpp` 的 `rule_order_valid`，五条谓词与
  驱动逐条对应（协议、有效地址族、端口区间相交、网段前缀相交、进程选择器）。进程选择器按
  **只折叠 ASCII** 比较（与 `matcher` 比较连接侧路径的方式一致；规则池正常路径下已被归一化，
  但注入不折叠的解析器时这条仍然正确）。
  **差异点**：驱动在 `allocate_rule_table` 里对 blob 的进程串再做 UTF-8 合法性/长度校验，C# 侧在
  编译期已经拒绝非法 UTF-8 与 >511 字节，因此产品路径不会走到驱动那条拒绝。
- **兜底动作矛盾**：`fallbackAction = block` 且存在任何非 block 规则 → 拒绝。驱动会在
  `ALE_AUTH_CONNECT` 生成无条件 block filter，让这些规则永远失效，而 classify 仍记
  `permitted` / `matched_permit`（计数撒谎）。驱动侧后来也加了同一道闸，C# 侧先在编译期报人话。
- 端口区间颠倒、族与网段不一致、未知枚举值、进程路径 >511 字节（UTF-8，不含 NUL）等。

`vm-check` 的差分矩阵对同一份规则表比较「C# 编译结论」与「驱动 `SetRuleBlob` 结论」：
接受时 generation +1，拒绝时 generation 不变且 Win32=87（`STATUS_INVALID_PARAMETER`）。

## 3. 监听、重定向与中继

- v4 `0.0.0.0:0` 先绑定拿到端口 P，再绑 `[::]:P` 且 `DualMode = false`（双栈套接字会与 v4 抢端口）。
  失败时换端口重试。实测监听端点是 `0.0.0.0:P,[::]:P`，同一个 P 通过 IOCTL 下发给驱动。
- accept 后 `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` 读回 36 字节 `redirect_context`；
  有 `SIO_QUERY_WFP_CONNECTION_REDIRECT_RECORDS` 就把它挂到出站 socket。
- **实测发现**：中继的出站连接带 records 时，驱动是**闸 2（redirect state）**放行的，
  `permitted_self_process` 保持 0。records 实测约 854–860 字节（同一台机上 V4/V6 都是 860，
  独立复现为 854，随连接记录内容略有差异）。不带 records 的路径才靠闸 4（self-PID）。
  E2E 两轮分别覆盖：第一轮带 records（`redirect_state +2`、`self_process +0`），
  第二轮 `--no-redirect-records` 走闸 4（`self_process +1`、`redirect_state +0`）。
  拆掉 self-PID 闸或不让产品登记自己，第二轮的出站连接会被再次重定向，E2E 的 `accepted=1` 断言变红。


- 读不到 redirect context 的连接直接拒绝：它不是被重定向来的，没有原始目的地可转发。

## 4. 端到端结果（2026-10-21）

真实进程（`WinForward.Controller client`）连接回显服务端口 → 被重定向到控制器监听端口 →
控制器读回 `127.0.0.1:<echo>` / `::1:<echo>` → 建出站 → 1 MiB 双向转发。客户端与回显服务的
SHA-256 一致（`12541F23…6AB4`），控制器报告 `accepted=2 refused=0 bytes_in=2097152 bytes_out=2097152`，
驱动计数 `redirected +2`、`contexts_allocated +2`、`rules_replaced +1`。停止信号之后
`enabled=0`、旧监听端口释放；不卸载驱动重启后第二轮仍然成功（`accepted=1`，+1 MiB）。
第二轮用 `--no-redirect-records` 走闸 4：`permitted_self_process +1`、`permitted_redirect_state +0`。

**定向破坏**：在临时副本里把驱动 `probe_classify.cpp` 的 self-PID 分支改成 `self_process_hit = false`
（不动工作区），重建装载后跑「一个客户端 + 不带 records」：报告里 `relay accept #2` 的 pid 变成了
控制器自己，短时间内 `accepts=94`——闸 4 一拆，中继出站立刻被再次重定向成环。E2E 的
`accepted=1` 与 `self_process +1` 两条断言都会因此变红。


## 5. 踩过的坑

1. **报告文件被长期持有**：`StreamWriter` 的句柄会让外部 `File.ReadAllText` 撞 sharing violation
   （实测 Windows）。`ReportWriter` 改成逐行 `FileMode.Append` + `FileShare.ReadWrite|Delete`，
   脚本侧也用 `FileShare.ReadWrite` 打开。E2E 因此能边跑边读。
2. **`windows-vm run` 回显中文会乱码**（控制台代码页），断言不要匹配中文；断言消息里用 ASCII
   标识（`fallbackAction`、`#0`、`exit=`）。
3. **共享 VM 的服务名冲突**：`wfprobe` 服务与 `C:\Windows\System32\drivers\wfprobe.sys` 是固定的，
   多个批次并行时安装/启动会撞。`build-and-test.ps1` 改成等待 STOPPED、重试安装并打印 `sc` 输出；
   `sync.sh` 增加关键文件存在性检查（否则会看到 MSB1009 这种「源码缺失」假象）。
4. **单方向异常会把兄弟泵永久挂住**：最初用 `Task.WhenAll` 等两条泵，客户端 RST 之后
   `clientToServer` 结束、`serverToClient` 仍在等数据，会话与两条 socket 一直留到进程退出。
   改成每会话一个 linked CTS：正常 EOF 只做半关闭（另一方向继续），异常结束取消兄弟泵；
   半关闭后给 30 秒收尾窗口，超时则显式关两端。E2E 的半关闭路径（EOF → Shutdown(Send) →
   另一方向送完）仍然绿。回归测试在 `RelayPumpTests`（真实 loopback socket）：RST 后 1 秒内必须
   结束；半关闭路径数据必须完整。把收尾改回 `Task.WhenAll`，RST 用例立刻变红。

5. **`controller-ready` 打印太早**：最早在 `SetRuleBlob`/`enabled=1` 之前就发，E2E 拿它当建连信号，
   留下「说 ready 但驱动还没启用」的窗口（门禁偶发失败的来源）。现在放到 `VerifyConfig` 之后。


## 6. 未覆盖 / 遗留

- 兜底 `block` 的差分用例已补进 `vm-check`（两条：与 permit 规则共存被拒、与全部 block 规则共存被接受）；
  驱动侧后来也加了同一道闸，两边结论一致（拒绝时 generation 不变、Win32=87）。
- 并发会话数没有上限（`_sessions` 无界），也没有空闲超时；MVP 可接受，生产部署需要加连接上限。

- 511/512 边界用驱动诊断 IOCTL 验证（`CreateProcess`/`FwpmGetAppIdFromFileName0` 都不支持
  >MAX_PATH 的真实进程）；产品路径的拒绝由 C# 单测覆盖。
- SOCKS5、UDP、配置热重载不在本批。
