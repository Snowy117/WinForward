# D7 产品决策记录：WFP L2 是否正式替代 NDISAPI/WinpkFilter

- 日期：2026-10-23
- 状态：**未决（保留为 kernel edition 候选，不正式替代）**
- 相关：B6a/B6b/B6c、`verification.md` §13–§15、`tools/linux/forwarded_gate.py`

## 问题

WinForward.Kernel 的 WFP L2 后端是否正式取代 NDISAPI/WinpkFilter，成为 WinForward 的默认
入站截获机制？

## 当前证据

支持继续投入的证据：

- TCP/UDP 数据面、分片 raw 9 片、产品 UDP ASSOCIATE 拒绝、真实 TCP ACK、stop/UAF 池检查、
  混合 NBL/return_miss 计数已在 B6a 关闭或记录。
- B6b 多入口（Internal/External）、同入口多 L、回程 key 含 server、热替换和 StateCommand
  观测已 E2E；ABI v7 兼容旧尺寸前缀。
- Linux 门禁 `forwarded_gate.py` 已覆盖 core/fragment/permit-first/host/reject，可重复运行。
- `gate.ps1` 的 host/WFP 套件与池检查全绿；驱动可安装/卸载/回滚。

尚不足以替代的证据：

- **性能**：A5 只有单次、含 C# suite 中继的量级数据（1MiB TCP 0.8–4.5 MB/s，200×64B UDP
  burst 209pps；host 直连 12.9 MB/s、1590pps），噪声大，未用 iperf3/内核 sink，也没有 VM 侧
  可比的纯驱动 CPU。不能据此判断生产吞吐/CPU 优于 WinpkFilter。
- **LWF 可见性**：LWF 对注入帧的可见性未验证；关闭 LWF 的环境未测。
- **IPv6 多入口/多 L**：未验证；IPv6 分片仍是 1280 MTU 内核分片。
- **UDP 多 L 并发多目的/UDP 热替换**：未单独 E2E。
- **产品多 L**：`WfpController` 每个 ifindex 只解析一个 L；同 ifindex 多 L 目前是驱动/夹具能力，
  没有“按规则选 L”的产品配置字段。
- **config disabled 行为**：ABI v7 下 WFP filter 不再进入 classify，B6a 的 fail 断言不再成立；
  产品语义需要确认（是“不拦截”还是“no_config 计数”）。
- **集成/许可/运维**：WinForward.Runtime 集成、签名/服务/回滚流程、WinpkFilter edition 的
  edition parity 差分门禁都还没有产品级验收。

## 决策

**不正式替代。** WFP L2 保留为 **kernel edition 的候选后端**，继续以实验/灰度方式验证；
不承诺默认切换，不以 B6 的测试绿色替代产品决策。

## 采用条件（全部满足才重新评估）

1. 独立空闲机 + iperf3/内核 sink，固定 pps/包大小，至少 3 次采样；给出与 WinpkFilter 的
   吞吐/CPU/延迟对比，而不是 suite 夹具数字。
2. LWF 开/关两种环境下注入帧可见性与策略一致性有 E2E。
3. IPv6 多入口/多 L、UDP 多 L/热替换至少各一条 E2E；产品层支持按规则选 L。
4. config disabled/fail 语义与 host 路径对齐，并写入 spec。
5. WinForward.Runtime 集成 + 同一份配置的 edition parity 差分门禁通过。
6. 24h 稳定性/资源泄漏/回滚演练通过。

## 影响

- README/AGENTS/architecture 明确 Forwarded 为“已实现、边界已知的 kernel 候选”，不写
  “已替代 WinpkFilter”。
- 已知边界统一列在 `verification.md` §15；任何一条关闭后更新本记录。
