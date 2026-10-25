# 执行计划 — 驱动规则表与 IOCTL

1. `rule_table`：池内存持有 + rundown + 原子替换 + 失败不动旧表。
2. IOCTL：下发 / 回读 / 计数器。
3. classify 改造：`connection_view` 构造 + `match()` 调用 + 三个动作分支。
4. V6 callout 注册与双栈 classify。
5. 四道防循环闸，逐道做负向验证。
6. 计数器接线。
7. 测试程序扩展：下发真实 blob、三条规则场景、dual-mode IPv6 场景、原子替换压力。
8. 门禁：驱动构建 0 warning、签名、加载、卸载；`src/driver` clang-format 干净；有效行 ≤ 500。
9. 结论与遗留写进本任务 `research/`。

每道闸、每条规则分支都要有负向验证——破坏它，验证必须变红。

---

## 结果（2026-10-21）

全部 9 步完成，细节与实测数据见 `research/findings.md`。摘要：

- 规则表：池内存副本 + 自旋锁 + 引用计数 + 退役链（`EX_RUNDOWN_REF` 在 DISPATCH_LEVEL 不可用）；
  失败不改旧表；100 次替换 + 并发连接全绿。
- IOCTL：下发/回读规则表、配置、计数、事件；全部走 `caller_is_admin`，DACL 未放宽。
- V4/V6 双 callout；dual-mode IPv6 实测通过（`.NET TcpClient.Connect(string,int)`，
  context family=6 / original=`::ffff:1.2.3.4`）。
- block：redirect 层不执行拦截（实测），改为 ALE_AUTH_CONNECT 上的 `FWP_ACTION_BLOCK` filter；
  “不绕过 Windows 防火墙”实验通过。
- 四道闸：闸 1/3/4 正向 + 负向（变异）验证；闸 2 与闸 3 属同一条件的两种读法，
  单独拆闸 2 时由闸 3 兜底，用“2+3 同时拆 → 成环”覆盖组合行为（findings §5）。
- 计数器：classify / permitted / blocked / redirected / matched_* / fallback_* / 四闸细分 /
  deferred_blocks / rejected_blobs / context 分配；A8 逐场景断言。
- 门禁：0 warning、签名 Valid、load/unload 干净、VM boot time 未变、clang-format 干净、
  所有驱动文件有效行 ≤500。
- 变异测试：`tools/vm/mutation-test.ps1`，11 条变异逐条变红 + 恢复后的正向基线全绿。

## 追加（父代理裁决后）

- **block 顺序冲突**：驱动 `rule_order_valid` 在任何 block 规则之前存在“可能重叠”的
  permit/redirect 规则时整块拒绝（协议/地址族/端口/网段/进程五维判定）；测试侧
  `BlobBuilder.ValidateRuleOrder` 做同样的检查并抛出带 `#序号` 的错误。负向用例：
  `BlobBuilder.Build` 必须抛错且消息含两条序号；`BuildUnchecked` 造出的同一 blob 必须被驱动
  以 STATUS_INVALID_PARAMETER 拒绝、generation 不变。
- **非 ASCII 进程路径**：`collect_process_path` 改成手工 UTF-16 → UTF-8（代理对按标准展开，
  超 512 字节或孤立代理项整条不匹配、不截断）；block filter 的 ALE_APP_ID 条件用
  `RtlUTF8ToUnicodeN` 反向转换，失败拒绝整块 blob。实测：`C:\Users\张三\wfprobe.exe`
  的 block 规则命中（blocked-exit=3），换端口不误伤（allowed-exit=0）。
- **findings**：新增 §2.4（冲突拒绝）、§3 的 UTF-8 契约与中文实测、§10 构建/API 坑
  （`FWP_V4_ADDR_AND_MASK` 主机字节序、`RtlUnicodeToUTF8N` 只允许 PASSIVE、apphost 改名坑、
  redirect 层不拦截、变异必须可编译）；闸 2 明确写“只由组合用例覆盖，未单独钉住”。

## 追加（511 字节边界）

- 契约定死：进程选择器 UTF-8 ≤ 511 字节（不含结尾 NUL），缓冲区 512 字节含 NUL；超限整条
  不匹配（length=0），不截断。
- classify 的差一修掉：`length + encoded_length + 1 > 512` 才拒绝（511 通过）。
- block filter 的 UTF-16 容量与 classify 对齐：`(512-1)*2 = 1022` 字节 = 511 码元；转换失败
  拒绝整块 blob。
- C# `BlobBuilder.ValidateProcessSelectors` 拒绝 >511 字节的选择器（错误信息给出字节数与路径）。
- 新增诊断 IOCTL `ioctl_measure_process_path`（0x807，只读、admin-only）：用同一个
  `collect_process_path` 测 UTF-16 → UTF-8 的边界，实测 `utf8-511=511 utf8-512=0 emoji=6
  surrogate=0`（代理对 4 字节、孤立代理项整条不匹配）。
- 长路径进程无法端到端构造：`CreateProcess` 对 >MAX_PATH 的 exe 启动失败（实测），
  `FwpmGetAppIdFromFileName0` 对长路径也失败；因此边界证据落在转换函数本身（classify 调用的
  就是它），长路径 API 限制写进 findings §10。

## 追加（检查方修复清单）

- fallback=block 与任何 non-block 规则组合：驱动 `allocate_rule_table` 整块拒绝；C# `ValidateFallback`
  同样拒绝。A4 改为“全 block 表 + fallback=block”，补“命中规则仍走 matched_block”。
- enabled=0 时删除全部 block filter；enabled=1 时按当前表重建；与换表共用 `rule_mutex`；
  换表时若处于禁用态不创建 filter。用例 `[rules] disabled: filter-off=True filter-on=True`。
- 驱动侧 selector 校验：UTF-8 合法性 + ≤511 字节，失败整块拒绝（permit/redirect 同样）。
- 重叠判定按 ASCII 折叠比较（与 matcher 一致）。
- protocol=any 统一按 TCP 下发 `IP_PROTOCOL`（该层只观察 TCP）。
- 0x807 输出先 `RtlZeroMemory`。
- redirect 失败路径改成 permit + `redirect_failures`（该层不执行拦截，真实语义是直连）；
  Phase 0 文档的 fail-closed 表述已订正。
- 删除 Phase 0 遗留脚本 `edge-cases.ps1` / `cross-process.ps1`，README 注明。
- findings 订正：§2.4 fallback、§2.5 fail-open、§3 代理项补测、§5 闸 2 措辞、§7 A4/新证据、§8 16 条。
