# VM 侧脚本

把「同步源码 → 构建 → 签名 → 安装 → 驱动套件 → 用户态单测/差分/端到端 → 卸载」串成可复现路径。

## 同步源码

```bash
session="$(windows-vm connect)"
tools/vm/sync.sh "$session"
# 想隔离到别的目录（多个批次共用一台 VM 时建议这样做）：
tools/vm/sync.sh "$session" 'C:\wfbench\wfprobe-userland'
```

默认同步到 `C:\wfbench\wfprobe`。驱动工程用 `..\..\src\common` 指向共享头，所以远程目录里
必须同时有 `src\driver` 与 `src\common`；`src\userland` 也会一起上传（不含 bin/obj）。
脚本最后会确认关键文件都在，避免下一段构建读到还没展开完的目录树。

## 一键验证

```bash
windows-vm "$session" script tools/vm/build-and-test.ps1
# 隔离目录：
windows-vm "$session" run "& 'C:\wfbench\wfprobe-userland\tools\vm\build-and-test.ps1' -Root 'C:\wfbench\wfprobe-userland'"
windows-vm "$session" bye
```

脚本做这些事：

1. MSBuild 构建 `wfprobe.sys`（`/W4 /WX`），断言输出里没有 `warning C`；
2. 签名并断言 `Get-AuthenticodeSignature = Valid`；
3. 安装并启动，断言 `RUNNING`；记录 VM boot time；
4. `accesschk64.exe -q -o \Device\WfProbe`，断言含 SYSTEM / Administrators、不含 Everyone；
5. 构建 C# 测试程序；
6. `wfprobe rules`：规则表、非法 blob、三个动作、fallback、100 次替换、计数器；
7. `wfprobe selftest`：V4 重定向；
8. `wfprobe selftest --dual`：**dual-mode IPv6**（.NET `TcpClient.Connect(string,int)`）；
9. `wfprobe gates`：四道防循环闸；
10. `tools/vm/firewall-check.ps1`：**不绕过 Windows 防火墙**的实证；
11. 构建用户态（控制器、配置层、IOCTL 客户端、中继）；
12. `WinForward.Userland.Tests test`：配置、冲突检查、ABI 一致性、布局、差分夹具；
13. `vm-check`：ABI 握手、用真实 IOCTL 的缓冲区长度验证结构尺寸、511/512 边界、
    「C# 冲突检查 vs 驱动 `rule_order_valid`」差分矩阵；
14. `tools/vm/userland-e2e.ps1`：真实进程 → 重定向 → 控制器中继 → 回显（V4 + V6，各 1 MiB 校验和），
    以及「退出后 enabled=0、旧端口释放、不卸载驱动直接重启」；
15. 断言全部套件之后驱动仍 `RUNNING`、VM boot time 未变（没有蓝屏）；
16. 无论成功失败，结束时 `sc stop` + 等 STOPPED + `sc delete` + 删除 sys。

## 端到端脚本单独跑

驱动已 RUNNING、用户态已 `dotnet build -c Release` 后：

```powershell
# 驱动没装时先装（只做构建/签名/安装/启动，失败会重试并打印 sc 输出）
& "C:\wfbench\wfprobe-userland\tools\vm\load-driver.ps1" -Root "C:\wfbench\wfprobe-userland"

# 跑一遍端到端；-KeepWork 保留报告文件，最后一行会打印 E2E-WORK=<目录>
& "C:\wfbench\wfprobe-userland\tools\vm\userland-e2e.ps1" -Root "C:\wfbench\wfprobe-userland" -KeepWork
```


它自己起一个回显服务（独立进程、系统分配端口），用 `WinForward.Controller` 起监听与中继，
再用两个独立客户端进程（V4 与 V6，各 1 MiB 伪随机数据）对照 SHA-256；结束前恢复 `enabled=0`。
脚本里每条断言失败都会让退出码非 0；末行是 `E2E-RESULT=PASS/FAIL`（ASCII，便于跨控制台代码页解析）。

## 反向验证（Linux 侧）

```bash
tools/userland/verify-gates.sh
```

它逐条破坏被守的东西：改 C++ 头的 `static_assert`、改夹具的 ABI 版本、改产品常量、拆掉冲突检查的
进程谓词、让配置层静默忽略未知字段——每一处都必须让 `abi` / `test` 变红，还原后必须重新变绿。

## 防火墙实验

`firewall-check.ps1` 的三段：无防火墙规则时连接成功 → 加一条出站 Block 规则后连接被拒 →
撤掉规则连接恢复。中间那段是在我们的 AUTH_CONNECT block filter 活动时跑的吗？

> 注意：当前实现的 AUTH_CONNECT 只有 `FWP_ACTION_BLOCK` filter，没有 callout；它只会增加
> 拒绝、不会放行，因此不存在“终止型 PERMIT 截胡防火墙”的路径。脚本仍然用同一个
> `blockprobe`（先下发含 block 规则的 blob，再连一个不被规则覆盖的目标）来确保实验在
> block filter 活动时进行。

## 变异测试

```bash
windows-vm "$session" script tools/vm/mutation-test.ps1
```

逐条改驱动源码（block filter 不创建、redirect/block/permit 分支退化、闸 1/2/4 恒假、闸 2+3
同时拆、V6 filter 不挂、非法 blob 当成功、计数器记错），重建 + 签名 + 装载，跑对应套件并
期望**非 0**；每条结束后恢复源码。最后恢复源码重建并跑正向基线，确认测试本身全绿。

## 运行单个套件

驱动已 RUNNING 时：

```powershell
wfprobe rules
wfprobe selftest --remote 1.2.3.4:19003 --iterations 3
wfprobe selftest --dual --remote 1.2.3.4:19003 --iterations 3
wfprobe gates
wfprobe blockprobe --rule-port 65000 --remote 1.2.3.4:19003
wfprobe stats / events --count 32 / pooltag / appid

WinForward.Controller check --config policy.json
WinForward.Controller run   --config policy.json --stop-file stop.flag --report controller.txt
WinForward.Controller state --report state.txt
WinForward.Controller disable
```
