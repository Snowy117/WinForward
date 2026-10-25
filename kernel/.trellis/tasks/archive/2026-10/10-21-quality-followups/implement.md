# 实施计划 · 质量收尾遗留项

> 技术判断见 `design.md`；需求见 `prd.md`。按顺序执行，每一步的验证不通过就不进入下一步。

## 1. R1 池标签断言

- [x] 1.1 `tools/wfprobe/PoolTags.cs`：加零基线构造，注释说明「条目在第一次分配时创建、
  本次启动内保留」这一实测事实。
- [x] 1.2 `tools/wfprobe/RuleSuite.cs`：基线为 `null` 时按全零参与差值；结束读数缺失时
  单独断言「条目在测量窗口内可读」，保留分配/释放/outstanding 三条断言。
- [x] 1.3 宿主 `dotnet build tools/wfprobe/wfprobe.csproj -c Release` 通过（由 `check.sh --full`
  的 `wf_check` 覆盖）。

## 2. R2 驱动修复

- [x] 2.1 `probe_classify.cpp`、`rule_table.cpp`：`ExAllocatePoolWithTag` 换成
  `ExAllocatePool2(POOL_FLAG_NON_PAGED, ...)`，删掉只为旧 API 的 `4996` 抑制。
- [x] 2.2 `probe_driver.cpp`：用 `extern "C" DRIVER_INITIALIZE DriverEntry;` 声明入口，
  定义处的 SAL（`_IRQL_requires_same_`、`_IRQL_requires_(PASSIVE_LEVEL)`）与 typedef 对齐
  （否则 PREfast 报 C28252）。
- [x] 2.3 `probe_control.cpp`：`ioctl_measure_process_path` 改为局部结构承接、整体拷回。
- [x] 2.4 `src/driver/README.md`、`tools/windows/README.md`：写最低系统版本（ExAllocatePool2）
  与 CodeQL 基线的新计数；逐条结论见 `codeql-baseline.md`。

## 3. 验证（Windows VM）

- [x] 3.1 同步仓库到 `C:\wfcheck\wfprobe`，跑 `codeql.ps1`：`FINDINGS results=14` → `PASS`（0 条）；
  抽取自检 successes=1 / failures=0。修改前后各跑一次，均为重建数据库。
- [x] 3.2 重启 VM（`WfPr` 条目不存在，复现原始失败条件），连续跑 5 次
  `gate.ps1 -Remote 1.2.3.4:19003`：5/5 `GATE-RESULT=PASS`，每轮 249 条断言全过。
  断言数由 248 变 249：池标签那组原先是「成功 3 条 / 失败 1 条」的二分，现在固定 4 条
  （多了「测量窗口内可读」），强度不降反升。
- [x] 3.3 第一轮（本次启动首次使用池标签）单独看 rules 套件输出：
  `池标签在测量窗口内可读 (ok)`、分配/释放次数 `expected=2 actual=2`、`outstanding 0`。
- [x] 3.4 5 轮之后卸载驱动，池标签无泄漏：`WfPr 65/65/0`、`WfTr 920/920/0`。

## 4. 验证（宿主）

- [x] 4.1 `tools/check.sh --full` 通过：8 PASS，0 FAIL。
- [x] 4.2 `git status` 只含本任务预期的文件；提交前 `tools/check.sh --staged` 通过。

## 5. 收尾

- [x] 5.1 任务文档 `codeql-baseline.md` 记录 14 条的逐条结论（规则 / 位置 / 结论 / 理由）。
- [ ] 5.2 归档任务并写 journal（`trellis-finish-work` 流程）。

## 回滚点

- R2 的驱动改动都在单个翻译单元内，任一步导致门禁失败即可用 `git checkout -- <file>`
  回滚该文件后重跑；CodeQL 数据库在 `build\codeql\`，删掉即重建。
- VM 状态可回到 `驱动开发二` 检查点（`windows-vm reset` 会清到初始工具状态，慎用）。