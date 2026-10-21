# CodeQL 基线 14 条逐条评审

> 基线：CodeQL CLI 2.27.3，`microsoft/windows-drivers:windows-driver-suites/recommended.qls`
> （106 条查询），未修改的驱动上 14 条结果。本文件按 `prd.md` R2 的要求给每条结果一个结论。
> 重新验证见文末。

## 逐条结论

| # | 规则 | 位置 | 结论 | 理由 |
|---|---|---|---|---|
| 1 | `cpp/drivers/wdk-deprecated-api` | `src/driver/probe_classify.cpp:294` | 修 | 重定向 context 改用 `ExAllocatePool2(POOL_FLAG_NON_PAGED, ...)`；对象随后被整体写入，零初始化不改变语义 |
| 2 | `cpp/drivers/wdk-deprecated-api` | `src/driver/rule_table.cpp:245` | 修 | 规则表同样改用 `ExAllocatePool2`；`RtlZeroMemory` 只覆盖头部，行为不变 |
| 3 | `cpp/drivers/illegal-field-access-2` | `src/driver/probe_driver.cpp:66`（`MajorFunction[IRP_MJ_CREATE]`） | 修 | `DriverEntry` 补 `extern "C" DRIVER_INITIALIZE DriverEntry;` 角色类型声明，查询的 `calledFromDriverEntry` 才能沿调用链认出 `create_device` |
| 4 | `cpp/drivers/illegal-field-access-2` | `src/driver/probe_driver.cpp:69`（`MajorFunction[IRP_MJ_CLOSE]`） | 修 | 同上 |
| 5 | `cpp/drivers/illegal-field-access-2` | `src/driver/probe_driver.cpp:72`（`MajorFunction[IRP_MJ_DEVICE_CONTROL]`） | 修 | 同上 |
| 6 | `cpp/drivers/illegal-field-access-2` | `src/driver/probe_driver.cpp:404`（`DriverUnload`） | 修 | 同上（赋值在 `probe_driver_entry`，是 `DriverEntry` 的直接调用链） |
| 7 | `cpp/microsoft/public/likely-bugs/memory-management/unprobeddereference` | `src/driver/process_path.cpp:14` | 修 | 见下方“根因 3”：输出在局部 `process_path_measurement` 里生成，指针不再来自 IOCTL 缓冲区 |
| 8 | 同上 | `src/driver/process_path.cpp:35` | 修 | 同上 |
| 9 | 同上 | `src/driver/process_path.cpp:42` | 修 | 同上 |
| 10 | 同上 | `src/driver/process_path.cpp:50` | 修 | 同上 |
| 11 | 同上 | `src/driver/process_path.cpp:88` | 修 | 同上 |
| 12 | 同上 | `src/driver/process_path.cpp:93` | 修 | 同上 |
| 13 | 同上 | `src/driver/process_path.cpp:97` | 修 | 同上 |
| 14 | `cpp/microsoft/public/likely-bugs/memory-management/usermodememoryoutsidetry` | `src/driver/probe_control.cpp:304` | 修 | 同一数据流的调用点；改成局部结构后调用点不再触碰“用户内存” |

三类根因（细节见 `design.md` §2）：

1. **弃用 API**：WDK 要求 `ExAllocatePool2`。代价是最低系统版本抬到 Windows 10 2004，
   已在 `src/driver/README.md` 写明。
2. **角色类型声明**：`WdmDriverEntry` 只认声明项上的 `DRIVER_INITIALIZE` **typedef**，
   不认 SAL 注解 `_Function_class_(DRIVER_INITIALIZE)`。补一条 WDK 标准声明即可，
   同时让 PREfast 的 C28175 语境判定更准确。
3. **IOCTL 缓冲区里的数组被当成用户指针**：查询的 `nestedMemoryOrigin` 会把
   `process_path_measurement.utf8`（`char[512]`，落在 `SystemBuffer` 里）当作“从用户内存读到的
   指针”，于是 7 处写入加 1 处调用被判未探测解引用。该 IOCTL 是 `METHOD_BUFFERED`，
   `SystemBuffer` 是内核副本，结论是假阳性；但与其抑制，不如让数据流不存在：
   `ioctl_measure_process_path` 在局部 `process_path_measurement` 里生成结果再整体
   `RtlCopyMemory` 回缓冲区。

## 重新验证

2026-10-21，同一套件、同一台 VM（重建数据库，含追踪构建）：

```
===== 结果摘要 =====
SARIF：C:\wfcheck\wfprobe\build\codeql\wf.sarif（380.7 KB，1 个 run）
结果条数：0
抽取校验：extractor-successes=1，extractor-failures=0
抽取校验通过。
没有结果（基线）。
CODEQL-RESULT=PASS
```

修过的规则全部不再出现：基线 **14 → 0**。上面这次是最终源码（含 `DriverEntry` 的 SAL 对齐）
重建数据库后的结果；改动前的基线是 14 条（同一脚本、同一台 VM）。

门禁侧：重启 VM 后连续 5 次 `tools/windows/gate.ps1 -Remote 1.2.3.4:19003` 全部
`GATE-RESULT=PASS`（每轮 249 条断言）；宿主 `tools/check.sh --full` 8 PASS / 0 FAIL。
