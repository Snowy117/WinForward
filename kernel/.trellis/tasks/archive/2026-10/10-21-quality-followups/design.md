# 设计 · 质量收尾遗留项（池标签断言与 CodeQL 基线）

> 父任务 `10-21-quality-gates-modernization` 的验收遗留。本文件只记录技术判断与取舍，
> 需求与验收口径见 `prd.md`。

## 1. R1 · 池标签断言偶发失败

### 1.1 事实

`tools/wfprobe/RuleSuite.cs` 在 `config.Enabled = 0` + `settle-ms` 之后读 `WfPr` 池标签，
期望同时拿到「禁用前基线」与「禁用后读数」：

- 基线 `poolBefore` 在套件开头（任何重定向之前）读取；
- 结束读数 `poolAfter` 在禁用并等待 `settle-ms` 之后读取；
- 任一为 `null` 就落到 `Check.True(false, "池标签可读", ...)` 的失败分支。

`WfPr`（`pool_tag = 'rPfW'`）只由重定向 context 的 `ExAllocatePoolWithTag` 使用；
规则表用的是另一个标签 `WfTr`（`rule_pool_tag = 'rTfW'`）。因此 `WfPr` 条目的存在
只取决于「本次启动以来是否发生过重定向分配」。

在 WinLTSC 上实测（驱动已卸载、无任何存活分配）：

```
[pooltag] tag=WfPr nonpaged_allocs=39 nonpaged_frees=39 nonpaged_outstanding=0
[pooltag] tag=WfTr nonpaged_allocs=552 nonpaged_frees=552 nonpaged_outstanding=0
```

结论：`SystemPoolTagInformation` 的条目在归零后**不会立刻消失**，本次启动内保留；
第一次分配才创建条目。于是失败只发生在「本次启动里第一次跑到该套件」的那一轮——
条目尚未创建，基线为 `null`；随后各轮条目已存在，基线可读，断言通过。
这与实测「第一次 FAIL、后两次 PASS」完全一致，**不是**驱动清理与查询之间的时序竞态。

### 1.2 取舍

- **选**：把 `null` 基线解释为「计数从 0 起算」（条目尚未创建），继续用同一套差值
  断言；结束读数仍要求可读，若在测量窗口内消失（条目被系统清掉）则显式失败并说明
  计数不可判定。这样既保留 `分配次数 / 释放次数 / outstanding = 0` 三条强度不降的
  断言，也不再依赖「这一轮之前有没有人用过该标签」。
- **不选**（`prd.md` 的方案 1：把查询挪到禁用之前）：`WfPr` 是 context 标签、`WfTr` 才是
  规则表标签，禁用时释放的规则表内存不会让 `WfPr` 变得可读；挪查询位置解决的是
  另一个问题。
- **不选**（方案 2：`null` 就跳过）：会让「首次运行」永久丢掉三条断言，等于降低强度。
- **不选**（方案 3 的字面形式：只断言禁用后 outstanding 为 0）：丢掉分配/释放次数
  断言，同样降低强度；本设计保留其语义（窗口结束后不为负增长）。

### 1.3 落地

- `PoolTags.cs` 增加零基线构造（`PoolTags.Zero(tag)`），把「没有条目」与「计数为 0」
  在类型上合并。
- `RuleSuite.cs` 用零基线参与差值断言；结束读数缺失时新增一条显式断言说明「条目在
  测量窗口内消失」，不把它伪装成计数为 0。

## 2. R2 · CodeQL 基线 14 条

基线（CLI 2.27.3，`microsoft/windows-drivers:windows-driver-suites/recommended.qls`，
未修改的驱动）：14 条。逐条定位后只有三类根因。

| 规则 | 条数 | 位置 | 根因 |
|---|---|---|---|
| `cpp/drivers/wdk-deprecated-api` | 2 | `probe_classify.cpp:294`、`rule_table.cpp:245` | 使用 `ExAllocatePoolWithTag` |
| `cpp/drivers/illegal-field-access-2` | 4 | `probe_driver.cpp:66/69/72/404` | 查询认不出 `DriverEntry` 的角色类型 |
| `cpp/microsoft/.../unprobeddereference` | 7 | `process_path.cpp:14/35/42/50/88/93/97` | IOCTL 输出结构里的 `utf8` 数组被当成「用户内存里的指针」 |
| `cpp/microsoft/.../usermodememoryoutsidetry` | 1 | `probe_control.cpp:304` | 同上，同一个数据流 |

### 2.1 `wdk-deprecated-api`：修

改用 `ExAllocatePool2(POOL_FLAG_NON_PAGED, size, tag)`：

- 两处分配的对象都是「先整体写入再使用」，`ExAllocatePool2` 的默认零初始化不改变语义；
- 释放仍是 `ExFreePoolWithTag` / WFP 内部的释放，标签计数不受影响（池头仍记录标签，
  `WfPr` 的 allocs/frees 断言继续成立）；
- **兼容性代价**：`ExAllocatePool2` 需要 Windows 10 2004（19041）及以上。本驱动当前只在
  Windows 11 上验证，README 里把最低系统版本写清楚，属于显式的产品决定而不是顺手改。

### 2.2 `illegal-field-access-2`：修（补上角色类型）

查询的 `calledFromDriverEntry` 依赖 `WdmDriverEntry`，而后者要求函数的**声明项**带
`DRIVER_INITIALIZE` 这个 typedef 类型。现有代码只有 SAL 注解
`_Function_class_(DRIVER_INITIALIZE)`，CodeQL 的提取器不把它当作 typedef 类型，
于是 `DriverEntry` 不被认成入口，`probe_driver_entry` / `create_device` 里的
`DriverObject->MajorFunction`、`DriverUnload` 赋值全部被判为“不在 DriverEntry 上下文”。

修法是 WDK 的标准写法：用角色类型声明入口

```cpp
extern "C" DRIVER_INITIALIZE DriverEntry;
```

再定义 `DriverEntry`。这条声明同时让 PREfast 的 C28175 语境判定更准确，属于真实改进。

### 2.3 `unprobeddereference` + `usermodememoryoutsidetry`：修（切断数据流）

`probe_control.cpp` 把 `SystemBuffer` 强转成 `process_path_measurement*`，再把其中的
`utf8` 数组地址交给 `probe_measure_process_path` 写入。查询把「从 IOCTL 缓冲区里取出的
指针」一律当成用户态指针（`nestedMemoryOrigin`），于是 7 处写入加 1 处调用被判为
未探测解引用。实际上该 IOCTL 是 `METHOD_BUFFERED`，`SystemBuffer` 是内核侧副本，
这两类结论都是假阳性。

不引入抑制，而是让数据流不存在：在 IOCTL 里用**局部** `process_path_measurement`
承接转换结果，再整体 `RtlCopyMemory` 回 `SystemBuffer`。指针不再从 IOCTL 缓冲区流向
被解引用的写入点，查询自然不再报；同时输出缓冲区在成功路径上才被覆盖，
语义更干净。

### 2.4 若仍有残留

按 `prd.md` 的要求逐条给出「修 / 抑制 / 接受」，抑制只写在被抑制的代码处并附理由，
不做集中白名单。

## 3. 验证口径

- R1：`gate.ps1` 连续 5 次 PASS，其中第一轮在重启后跑（覆盖「本次启动首次使用」）；
  另跑一次「先跑一轮、再跑一轮」确认结果与顺序无关。
- R2：重跑 `codeql.ps1`，结果条数下降；修过的规则不再出现；`CODEQL-RESULT=FINDINGS results=N`。
- 每轮门禁后确认 `gate.ps1` 的 248 条断言不减少（本轮不增删门禁断言）。
- 宿主侧 `tools/check.sh --full` 通过。
