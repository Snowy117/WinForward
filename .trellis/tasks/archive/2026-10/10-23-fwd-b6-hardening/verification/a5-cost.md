# A5 成本基线（Forwarded TCP/UDP，2026-10-23）

## 方法

- 环境：Linux `192.168.100.4` ↔ Windows VM `192.168.100.2`（WinLTSC），1GbE vSwitch，
  无 CPU 隔离、无专用网卡、单次采样；后台有测试工具与 tmux 会话。
- forwarded 路径：Linux 客户端 → `192.168.100.99`（入口别名）→ WFP DNAT → `wfprobe` suite
  listener（TCP 40400 / UDP 40400）→ Linux echo backend `192.168.100.4:40250`；回程由驱动
  注入/还原。forwarded 的数字包含 **C# suite 中继**，不是纯驱动成本。
- host 对照：Linux 客户端直连 `192.168.100.4:40250` 的 echo，不经 VM。
- TCP：1MiB，`forwarded_tcp_client.py`（verify 端口 41400）；host 用 `tcp_echo_client.py`。
- UDP：200 个 64B datagram，burst（`--interval 0`）；forwarded 用 suite core，host 直连 echo。
- 原始日志：`verification/a5-cost/`（`run.log`、`results.json`、`forwarded-*.log`、`host-*.log`）。

## 结果（单次，量级）

| 路径 | 1MiB TCP | 客户端 CPU | 200×64B UDP | 客户端 CPU |
|---|---:|---:|---:|---:|
| forwarded（经 VM + suite） | 1.30 s / 0.81 MB/s | 1.14 s user+sys | 0.96 s / 209 pps | 1.23 s user+sys |
| host 直连 echo | 0.081 s / 12.9 MB/s | 未单独采 | 0.126 s / 1590 pps | 未单独采 |

前一次 paced 采样（1MiB TCP、200 UDP @10ms）得到 forwarded 4.5 MB/s、host 8.4 MB/s，
说明单次采样噪声很大；不能把某一组数字当作稳定基线。

## 结论与边界

- 这组数字只用于**量级参考**：forwarded 在本夹具下比直连慢约 1.5–16 倍（取决于 TCP 大小/
  burst），主要成本在 suite 的 C# 中继、每包用户态转发和 VM vSwitch；**不是驱动单独成本**。
- 没有硬性 kill criteria；B3 的 58.1% 只作参考，不用于准入。
- 未使用 iperf3（两端都没有安装），也未做内核 sink；UDP burst 在更高数量（1000+）出现客户端
  收包不完整，属于夹具/缓冲上限，不能据此算“转发丢包”。
- 未取得可信的 VM 侧 wfprobe CPU（查询采样为 0）；TCP/UDP 客户端 CPU 也不能与内核态直接相加。
- 结论：A5 **有量级数据，但没有可复现的稳定基线**；若用于产品决策，需要独立空闲机、
  iperf3/内核 sink、固定 pps/包大小并多次采样。此限制已写入 B6c 最终报告。
