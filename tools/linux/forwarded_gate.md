# Forwarded Linux 门禁

`tools/linux/forwarded_gate.py` 在 Linux 客户端侧驱动 Windows VM 上的 `wfprobe` suite，
自动 prepare/cleanup，按阶段跑 forwarded 数据面。CI 用法与本地相同，只是把三条远程命令
换成 CI 的 SSH/WinRM 包装。

## 前置

- Windows VM 已安装 ABI v7 的 `wfprobe.sys`，服务 RUNNING；`wfprobe.exe` 已构建。
- Linux 侧能访问 VM（默认 `192.168.100.2`），且原始目的地别名 `.99` 由门禁自己创建/删除。
- 远端三个命令（可由环境变量提供）：

```bash
export WF_GATE_RUN='windows-vm fast run'    # 同步执行 PowerShell
export WF_GATE_POLL='windows-vm b6ctl run'  # 另一个会话，用于轮询输出
export WF_GATE_UP='windows-vm up2 up'       # 上传本地文件到 Windows
python3 tools/linux/forwarded_gate.py
```

CI 中把三个变量换成等价的 `ssh`/`winrm` 包装；`--run-cmd/--poll-cmd/--up-cmd` 也可显式传。
门禁**禁止**任何阶段传 `--no-verify`；脚本会直接报错。

## 覆盖

| 阶段 | suite | 客户端 |
|---|---|---|
| `tcp-core` | forwarded-tcp core | `forwarded_tcp_client.py`（verify） |
| `udp-core` | forwarded-udp core | `forwarded_udp_client.py`（verify） |
| `fragment-ipv4` | forwarded-udp fragment | `raw_fragment_sender.py` 9×IPv4 |
| `permit-first` | fragment + permit-first 端口 | raw sender |
| `host-same-remote` | fragment-host | raw sender → 40600 |
| `reject` | B6a 产品 harness + fake SOCKS5 | UDP client → report |

`reject` 需要 `tools/linux/forwarded-udp-reject.json`；
可用 `--skip-reject` 跳过。

## 未自动化（明确）

- **fail 阶段**：ABI v7 下 config disabled 时 WFP filter 不再进入 classify，suite 的
  `no_config` 断言不成立；`--include-fail` 保留为诊断入口，当前不作为门禁通过条件。
  这是 B6c 发现，已记录，未改数据面。
- multi-ingress / hotswap / noingress / StateCommand：Windows-only wfprobe 阶段，B6b
  已单独验证；不在 Linux 门禁重复。
- IPv6 9 分片：需要客户端 IPv6 地址与 MTU 调整，见 `tools/linux/ipv6_mtu_fragment.py`，
  手工阶段。
- A5 成本：`tools/linux/a5_forwarded_cost.py` 是测量脚本，不是 pass/fail 门禁。

## 产物

默认写到 `verification/forwarded-gate/`：`run.log`、每阶段 `*-suite.log`/`*-client.log`、
`reject-*`、`prepare.log`。原始 suite 输出在 Windows `C:\workspace\fg-*.out`，正常结束会清理
别名和防火墙；`--keep-alias` 可保留现场。
