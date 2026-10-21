# 执行计划：Host UDP B2b

## 1. classify 与计数

- [x] `src/driver/probe_classify.cpp`
  - 协议早退改为“非 TCP 且非 UDP”。
  - facts 之后、`rules == nullptr` 之前：UDP `udp_classify_calls++`。
  - 三个 helper：`count_udp_permit` / `count_udp_block` / `count_udp_redirect`。
  - 所有最终 permit 路径调用 permit helper；block 调用 block helper；redirect 成功调用
    redirect helper；redirect 失败路径同时 permit helper。
  - `redirect_classification` 的 `destination.protocol` 按 `facts.protocol` 映射。
  - 更新 `permitted_non_tcp` 注释为“既非 TCP 也非 UDP”。
  - 加 `<mutation id="classify-udp-branch">` 与计数点标记。
- [x] 逐条审查返回路径，确认不变式；在临时 smoke 中断言增量。

## 2. block filter

- [x] `src/driver/block_filters.cpp`
  - 所有 filter 显式写协议条件 6/17；`any` 与 fallback 各展开两条。
  - 更新注释：不能省略协议条件（ICMP 风险）。
  - `max_block_filters` 64 → 128；`rule_applies_to_layer` 不变。
  - 加 `<mutation id="block-filter-protocol">` 区域。
- [x] 删除 `src/driver/rule_table.cpp` 的 `udp-block-temporary-guard` 整段与 mutation 标记。

## 3. wfprobe 测试辅助

- [x] `tools/wfprobe/BlobBuilder.cs` / `TestRule`：给 `Port`/`BlockPort`/`RedirectPort`/
      `PermitPort` 增加可选 `transportProtocol`（默认 6，保持现有 TCP 用例），或加
      `WithProtocol` helper。
- [x] 临时 smoke：在 `tools/wfprobe` 加一个临时 `udp-smoke` 命令（或临时 C# harness），
      按 design §7 验证；**跑完删除**，不提交。证据写到任务 `research/b2b-smoke.md`。

## 4. 宿主验证

- [x] `tools/check.sh --fast`；`tools/check.sh --full` 全绿。
- [x] C# 测试全过；TCP rules/selftest 的宿主部分不退化。
- [x] 临时 smoke 编译/运行（宿主 dotnet 10 不能编译 net11 的 wfprobe，改在 VM 编译/运行）。

## 5. VM 验证

- [x] 工作区 tar 同步到 VM `C:\workspace\WinForward.Kernel`；`load-driver.ps1` 构建/安装。
- [x] UDP redirect：unconnected + connected，listener 收 context protocol=17；`udp_redirected`。
- [x] UDP permit：listener 无包；`udp_permitted`；`fallback_action=redirect` 生效。
- [x] UDP block：send 成功、对端收不到、未 block 端口可达；`udp_blocked`/`deferred_blocks`。
- [x] `protocol=any` block：TCP AccessDenied、UDP 静默；两条都验证。
- [x] 热替换：长活 socket 先 permit，换 block 后旧关联/新关联分别记录。
- [x] 计数不变式：每次测试 reset stats，断言
      `udp_classify_calls == udp_permitted + udp_blocked + udp_redirected`。
- [x] TCP 回归：`rules` / `selftest` V4+V6 / `gates` 全 PASS；事件 protocol=6。
- [x] 跑完卸载；临时 smoke 文件从工作区删除；`git status` 只含核心改动。

## 6. 交付

- [x] 报告：改了哪些文件、VM 用例结果、热替换观察、计数增量、TCP 回归。
- [x] 不 commit；主会话提交。
- [x] B3 待办：正式 UdpSuite/gate、IPv6、池断言、U13 热替换固化、README/spec/可观测性。

## 回滚点

- 核心改动是 classify/block/rule_table；回滚整批 revert。B2a 的 ABI v3 不受影响。

## 交付记录（2026-10-22）

- UDP 已正式进入 classify；4 个计数器接上；B1 临时闸删除；`BlobBuilder` 支持协议参数。
- 首次检查要求把 `any`/fallback block 从“不写协议条件”改成 6/17 各一条，因为该层不写条件还会
  匹配 ICMP；已改，`max_block_filters=128`。VM 复核：`any`/fallback 下 TCP 拒、UDP 静默丢、
  **ICMP 2/2 reply 未被拦**。
- 宿主：`tools/check.sh --full` 8/8；C# 134/134；`ctest` 66/66。
- VM：UDP redirect/permit/fallback/block/any/热替换/计数不变式全 PASS；TCP rules/selftest
  V4+V6/gates 全 PASS（干净启动）。
- 热替换实测与初稿不同：旧关联后续 datagram 被新 auth filter 丢弃，但不重新 classify，
  `udp_calls`/`udp_blocked` 不增；已在父设计 §5.4/U13 与 `udp_blocked` 注释固化。
- 风险：connected UDP context 客户端退出 180s 后仍 outstanding、`sc stop` 后仍存在；B3 先测。
- VM 期间发生一次 0x11D/0xc000021a 崩溃，证据见 `research/vm-crash.md`，现有证据不支持归因
  wfprobe；B3 用 checkpoint/稳定快照。
- 留 B3：connected 池释放窗口、U3 回包 classify 扣减、mutation 接入、`control --clear-self`、
  PrintEvent/PrintStats/StateCommand 可观测性、README/spec。
