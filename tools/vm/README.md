# VM 侧脚本

这两个脚本把「同步源码 → 构建 → 签名 → 安装 → 三条验证 → 卸载」串成一条可复现的路径。

## 同步源码

```bash
session="$(windows-vm connect)"
tools/vm/sync.sh "$session"
```

默认同步到 `C:\wfbench\wfprobe`。驱动工程用 `..\..\src\common` 指向 `redirect_context.h`，
所以远程目录里必须同时有 `src\driver` 与 `src\common`；`sync.sh` 会一起打包。

## 构建与验证

```bash
windows-vm "$session" script tools/vm/build-and-test.ps1
windows-vm "$session" bye
```

脚本做这些事：

1. MSBuild 构建 `wfprobe.sys`；
2. 用证书库里的 `CN=WinForward Test Driver` 签名并检查 `Get-AuthenticodeSignature` 为 `Valid`；
3. 安装并启动 `wfprobe` 服务，确认进入 `RUNNING`；
4. 构建 C# 测试程序；
5. 跑三条验证：ANY → loopback、指定 local 地址、默认 200 次连接的池用量；
6. 无论成功失败，结束时 `sc stop` + `sc delete`。

参数可在脚本里改（`$Remote`、`$ListenerPort`、`$LocalBind`、`$Iterations`）。

## 补充验证

`build-and-test.ps1` 末尾会依次调用下面两个脚本；也可以单独运行（前提是驱动已 RUNNING）：

- `cross-process.ps1`：listener 与 client 分属两个进程，driver 配置里 `target-pid` 与
  `listener-pid` 分开，验证真实代理形态（本次实测 listener pid=1548、client pid=6400，
  context 读回 pid=6400、original=1.2.3.4:443，客户端收到 echo）。
- `edge-cases.ps1`：重定向目标端口无人监听时的 context 释放（池计数 +1/+1、outstanding 0）。

`build-and-test.ps1` 里还会用 `accesschk64.exe -q -o \Device\WfProbe` 打印设备 DACL；
正常结果应只有 `NT AUTHORITY\SYSTEM` 与 `BUILTIN\Administrators`，没有 Everyone。

