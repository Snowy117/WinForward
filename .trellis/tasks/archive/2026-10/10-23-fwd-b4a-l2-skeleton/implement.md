# 执行计划：B4a 驱动 L2 骨架与 Forwarded pass/block

> 先驱动骨架，再用户态 IOCTL/工具，最后 VM 验证。B4a 不做 DNAT/注入/用户态中继。

## 0. 读上下文

- B3 归档研究：`.trellis/tasks/archive/2026-10/10-23-fwd-b3-l2-spike/research/spike-l2.md`
  与 `environment.md`（重点 §6.1 六条 B4 硬约束、layer 58/59、NBL 解析、防环）。
- 现有驱动：`src/driver/probe_driver.h`、`probe_driver.cpp`、`probe_classify.cpp`、
  `probe_control.cpp`、`rule_table.cpp`、`probe_ioctl.h`。
- B1 规则层：`src/common/forwarded_blob.h`、`forwarded_matcher.h`。
- B2 配置/接缝：`Forwarded/IAdapterIdResolver.cs`、`ControllerSettings`、`WfpDeviceClient`。
- 构建/安装：`tools/windows/WfBuild.psm1`、`tools/windows/load-driver.ps1`、`gate.ps1`。
- 本任务 `prd.md`、`design.md`。

## 1. 驱动（Linux 编辑，VM 构建）

- [x] `probe_forwarded.h/cpp`：forwarded 规则表（blob 副本 + generation + refcount）、计数、
      callout 注册/注销。
- [x] 注册 `FWPS_LAYER_INBOUND_MAC_FRAME_NATIVE` / `OUTBOUND_MAC_FRAME_NATIVE`；
      与现有 ALE callout 共存，卸载逆序。
- [x] classify：防环查询 → NBL 解析（VLAN/IPv4/IPv6/TCP/UDP）→ `match_forwarded` →
      permit/block/fallback → 计数。
- [x] IOCTL/ABI v4：`ioctl_set_forwarded_blob`、`ioctl_get_forwarded_info`、
      `ioctl_get_forwarded_stats`；`probe_abi_version = 4`；`static_assert` 布局。
- [x] 非法 blob 整块拒绝、旧表不动；generation 递增。
- [x] 不修改帧、不注入；B4b 再接线。

## 2. 用户态（Linux 编辑，dotnet 测试）

- [x] C# ABI 镜像：`ProbeForwardedInfo`、`ProbeForwardedStats`、IOCTL 常量；布局测试。
- [x] `WfpDeviceClient` 增加 `SetForwardedBlob`、`GetForwardedInfo`、`GetForwardedStats`。
- [x] `WindowsAdapterIdResolver`：stable ID/名字 → ifIndex；纯映射逻辑可单测。
- [x] `RunCommand`/`CheckCommand` 在 Windows 传入真实 resolver；forwarded blob 下发路径
      （B2 已编译，B4a 接 IOCTL；失败 fail closed）。
- [x] `ControllerSettings`/控制器启动顺序：先下发 forwarded blob 再启用？按现有 host 顺序，
      确保非法 blob 不半启动。

## 3. wfprobe

- [x] `tools/wfprobe/ForwardedSuite.cs` + `forwarded-rules` 命令：F1–F7（见 design §5）。
- [x] Linux 客户端：用临时 python socket 客户端跑 F1i/F2i（连接/超时），block 断言超时而不是 RST；
      两段式用法写在 `src/driver/README.md` 的「验证」小节。
- [x] 统计读取与断言；非法 blob 用例检查 generation。

## 4. VM 验证

- [x] 先确认 VM checkpoint/回滚点；`load-driver.ps1` 构建/安装 ABI v4。
- [x] `wfprobe forwarded-rules` F1–F7；host 既有 `wfprobe rules` 仍 PASS。
- [x] Linux 客户端：pass 连接成功；block 超时；计数正确。
- [x] 卸载/重装一次，确认 callout 注销无残留。
- [x] 不 reset VM，除非驱动无法卸载。

## 5. 复核修复（checker 结论「有条件通过」）

- [x] F2/F4 block 规则改用固定源端口 + 端口收窄，不再命中 WinRM；套件级恢复看门狗。
- [x] forwarded 计数改为「本地累加 + 锁内一次性发布」，snapshot/reset 同锁；跨 reset 恒等式稳定。
- [x] 空 forwarded 表路径也回读 `installed/规则数/fallback/total_size/generation`。
- [x] `tools/check.sh` 与 `tools/lint/forbidden.py` 覆盖未跟踪的新源文件（`--others --exclude-standard`）。
- [x] SKIP 不再当 PASS：必需用例被跳过时 `verdict=SKIPPED`、退出码 6；F7 记为环境未验证。
- [x] `gate.ps1` 纳入 `forwarded-rules --only F1,F2,F3,F6`，失败即 gate 失败。
- [x] 防火墙表述改为「L2 层没有防火墙 filter，防火墙在 ALE 层」；`redirect` 在 Check/Run 阶段提前拒绝。

## 6. 文档与门禁

- [x] `src/driver/README.md`：L2 callout、layer 58/59、NBL 解析、block 语义（不注入 RST）、
      防环、B4b/B4c 边界、ABI v4。
- [x] `wfp-architecture.md` 补 B4 事实（可留到 B4b/B6，但本批至少记录 L2 层字段限制）。
- [x] Linux：`cmake --build build --target wf_check`、`tools/check.sh --full`。
- [x] 报告：改动、验证、未覆盖/风险。

## 回滚点

- 驱动安装前保留上一个签名版本；L2 callout 出问题先 disable forwarded（保留 host），
  不影响 ALE 路径。
