# C++ 编码规范

> 适用范围：本仓库全部 C++ 翻译单元。
> 拟定：2026-10-20。

本仓库同时编译两种目标——Windows 内核驱动与 Windows 用户态程序。它们共用一套命名与组织约定，但语言能力相差很远。这份文档先说清两套方言的边界，再给出可执行的规则。

---

## 1. 两套方言

| 目录 | 方言 | 编译器 |
|---|---|---|
| `src/driver/` | 内核方言（C++23 的受限子集） | MSVC + WDK，`/kernel` |
| `src/userland/` | 完整 C++23 | MSVC |
| `src/common/` | **按内核方言约束** | 两侧都会编译 |
| `tests/host/` | 完整 C++23 | clang（Linux 宿主机，见 `flake.nix`） |

`src/common/` 是唯一的共享面。它会被内核翻译单元包含，所以它只能使用两套方言的交集——写成内核方言，用户态那一侧天然可用。反过来不成立。

这条不对称是整个仓库最重要的结构约束：**共享代码往严格的方向写**。

---

## 2. 语言基线

- **用户态**：C++23。RAII 是默认的资源管理手段，不留手动释放路径。
- **内核**：C++23 的受限子集，具体边界见 §5。同样是 RAII 优先，只是可用的积木少得多。

---

## 3. 命名

**一句话规则：除宏之外，全部 snake_case。**

| 符号 | 规则 | 例 |
|---|---|---|
| 命名空间 | snake_case | `winforward::wfp` |
| 类 / 结构体 / 联合 / 枚举 | snake_case | `redirect_context`、`rule_table` |
| 类型别名（`using` / `typedef`） | snake_case | `using port_range = std::pair<uint16_t, uint16_t>;` |
| 函数 / 方法 | snake_case | `evaluate_host`、`apply_redirect` |
| 变量 / 参数 / 成员 | snake_case | `process_id`、`rule_index` |
| 常量（`constexpr` / `const`） | snake_case | `max_rule_depth`、`default_listener_port` |
| 枚举成员 | snake_case | `stream_state::idle` |
| 模板参数 | snake_case | `template <typename value_type>` |
| **宏** | **SCREAMING_SNAKE_CASE** | `WF_TRACE`、`WF_ASSERT_IRQL_PASSIVE` |

补充约定：

- **不加 `_t` 后缀**。`ip_prefix`，不是 `ip_prefix_t`——`_t` 与 Windows 的遗留类型（`DWORD`、`ULONG` 之外的那批）混在一起读不出信息。
- **缩写全部小写**。`ipv4`、`tcp`、`wfp`、`ndis`，不写 `IPv4`、`TCP`、`WFP`。全大写留给宏。
- **traits 沿用标准库惯例**：`is_redirectable_v`、`ip_version_of_t`。
- **不用匈牙利前缀**。`p`、`n`、`dw`、`sz` 一律不要——类型在声明处就写着。
- **名字表达意图，不表达实现**。`process_attribution_failed` 优于 `get_pid_error_2`。

### 3.1 本节只管 C++

上面这套 snake_case 规则适用于本仓库的 **C++ 翻译单元**。`src/userland/` 的 C# 代码按 .NET 生态的常规约定：类型、方法、属性、事件 `PascalCase`，参数与局部变量 `camelCase`，实例私有字段 `_camelCase`，**`private static readonly` 字段用 `PascalCase`**，接口 `I` 前缀。

两侧命名风格**有意不同**。两个语言共享一份 ABI，但强迫 C# 迎合 C++ 的命名会同时得罪两边的工具链与读者；约定只需在各自的语言内自洽。

---

## 4. 文件有效行

**有效行的定义**：文件中去掉空行、以及整行都是注释的行之后剩下的行数。行尾注释不计入额外行。

**单个文件的有效行上限：500 行**（头文件与实现文件分别计算，`.inl` 计入其主头）。

超过上限不是"允许但要注意"，是需要处理的设计信号，二选一：

1. 拆文件——通常说明这个文件承担了两个以上职责；
2. 提取共同部分——通常说明有重复结构没有抽象出来。

确有必要超过时，在文件头部注释里写明理由，并说明为什么拆分不合适。**没有理由的超限不接受。**

---

## 5. 内核方言

内核态没有异常运行时、没有 RTTI 支持、IRQL 之上不能分页。以下限制不是风格偏好，是能否通过编译或能否稳定运行的边界。

### 5.1 禁止

| 禁止 | 原因 |
|---|---|
| `throw` / `try` / `catch` | 内核 C++ 不产生异常运行时；抛异常等于立即崩溃 |
| `dynamic_cast` / `typeid` | RTTI 关闭 |
| 标准库容器与字符串（`std::vector`、`std::string`、`std::map` …） | 依赖堆分配与异常，且官方不支持 |
| `new` / `delete` | `/kernel` 下不可用；内存一律走池分配 |
| 全局或静态对象的**动态初始化** | 构造时机在 `DriverEntry` 之前，不受控 |
| **函数内静态局部变量** | 首次调用需要线程安全的初始化守卫，在 `DISPATCH_LEVEL` 下不可用 |
| `__try` / `__except` 与"持有析构函数的对象"同处一个作用域 | SEH 展开不调用 C++ 析构函数，资源泄漏 |
| `DISPATCH_LEVEL` 及以上的浮点运算 | 内核栈不保存 FPU 状态 |

### 5.2 允许

- 类、继承（尽量不用虚函数）、模板、`constexpr`、`static_assert`、`enum class`。
- 无分配、无异常的基础设施头：`<cstddef>`、`<cstdint>`、`<limits>`、`<type_traits>`、`<utility>`、`<bit>`。
- `<new>` **仅用于 placement new**（在预分配的池块上构造对象），不用于 `operator new`。
- WDK 头：`<ntddk.h>`、`<ntifs.h>`、`<ntstrsafe.h>`、`<ntintsafe.h>`、`<fwpsk.h>`、`<fwpmk.h>`、`<wsk.h>`。

### 5.3 RAII 的形式

内核侧不引入 STL，RAII 由仓库自己的最小类型承担。它们按 `unique_ptr` 的语义设计——可移动、不可复制、析构即释放：

```cpp
// 池内存：析构调用 ExFreePoolWithTag
template <typename value_type>
class pool_ptr;

// 引用计数对象（callout、过滤器引擎句柄等）
template <typename value_type>
class object_ref;

// 锁与 rundown 保护
class irql_guard;
class rundown_guard;
```

规则：

- 分配之后立刻交给 RAII 对象，**不允许**"先分配、出错手动 `ExFreePool`"的写法。
- 失败路径靠作用域退出自动收敛；`goto cleanup` 只在跨 C 接口的极少数场景使用，且必须有注释说明。
- 跨 `<ntddk.h>` 边界的原生句柄，用窄接口的包装类型持有，不裸传。

### 5.4 语言版本口径

内核侧按 `/std:c++latest` 编译，但**只用 §5.2 白名单里的能力**。这条规则的执行方式是评审，不是编译器——编译器不会拦住 `std::vector`，但内核会。

### 5.5 WDK 约定与静态分析

**池内存用 `ExAllocatePool2`**。`ExAllocatePoolWithTag` 是 `cpp/drivers/wdk-deprecated-api` 的弃用 API，
新代码一律写成：

```cpp
auto* block = static_cast<block_type*>(ExAllocatePool2(POOL_FLAG_NON_PAGED, size, pool_tag));
```

`POOL_FLAG_NON_PAGED` 取代 `NonPagedPoolNx`，默认零初始化（只在需要局部清零时才保留 `RtlZeroMemory`）。
代价是最低系统版本抬到 **Windows 10 2004（10.0.19041）**；要覆盖更旧的系统，就在运行时用
`MmGetSystemRoutineAddress` 取 `ExAllocatePool2`、缺失时回退到 `ExAllocatePoolWithTag`。

**`DriverEntry` 必须用角色类型声明**：

```cpp
extern "C" DRIVER_INITIALIZE DriverEntry;

extern "C" _Function_class_(DRIVER_INITIALIZE) _IRQL_requires_same_ _IRQL_requires_(PASSIVE_LEVEL) NTSTATUS
    DriverEntry(_In_ PDRIVER_OBJECT driver_object, _In_ PUNICODE_STRING registry_path)
```

CodeQL 的 `WdmDriverEntry` 只认声明项上的 `DRIVER_INITIALIZE` **typedef**，PREfast 的 C28175 也按它
判定上下文：少了这行声明，`DriverEntry` 调用链里的 `DriverObject->MajorFunction` / `DriverUnload`
赋值会被判成越权访问。定义处的 SAL 要与 typedef 一致（`_IRQL_requires_same_`、
`_IRQL_requires_(PASSIVE_LEVEL)`），否则 PREfast 报 C28252。

**IOCTL 输出不要先强转 `SystemBuffer` 再把里面的地址交给别处解引用**。`METHOD_BUFFERED` 下
`SystemBuffer` 是内核副本，但 CodeQL 的 `nestedMemoryOrigin` 会把「从 IOCTL 缓冲区取到的指针」
当成用户态指针，对写入点报 `unprobeddereference`。约定写法是先在局部结构里生成输出，再整体
`RtlCopyMemory` 回缓冲区——失败路径也不会留下半成品：

```cpp
process_path_measurement measurement{};
probe_measure_process_path(utf16, input_length, measurement.utf8, measurement.utf8_length);
RtlCopyMemory(buffer, &measurement, sizeof(measurement));
```

---

## 6. 编译期计算

鼓励模板元编程与 `constexpr`，但不是为了炫技。

**应该放进编译期的**：

- 结构体的尺寸与布局约束——用 `static_assert(sizeof(redirect_context) == 32, "…")` 而不是运行时断言。
- 规则表的静态部分：CIDR 掩码、端口范围校验、偏移表。
- 字节序转换、位域提取的常量折叠。
- 类型分派的标签（`if constexpr`、变参模板展开），避免虚函数与函数指针表。

**不应该放进编译期的**：

- 会把调用栈变成模板实例化瀑布的东西。超过三层的递归模板实例化需要注释说明。
- 需要运行期数据才能决定的分支——`constexpr` 帮不上忙时，老实写运行时代码。
- 为了省几个周期而牺牲错误可读性的地方。`static_assert` 的第二个参数是给人看的，写清楚违反了什么约定。

模板实例化深度与目标文件体积是真实成本。内核镜像有大小预算，过度泛化会让 `.sys` 膨胀。

---

## 7. 错误处理

| 侧 | 约定 |
|---|---|
| 内核 | 返回 `NTSTATUS`；调用方用 `NT_SUCCESS()` 判断；**不吞错误** |
| 用户态 | 返回 `std::expected<value_type, error_type>`（C++23）；不用异常做控制流 |

两条共用要求：

- 失败路径必须留下可诊断的信息，但不要在 `DISPATCH_LEVEL` 以上写日志。
- 错误码不要自己发明体系。内核侧复用 `NTSTATUS` 值，用户态侧复用 `Win32` 错误码，只有真正业务性的语义才新增枚举。

---

## 8. 格式与门禁

### 8.1 工具与范围

- **格式化**：`.clang-format` 是唯一事实来源（Allman、4 空格、120 列、指针左贴类型）。clang-format 版本由 `flake.nix` 锁定在 21.x——换版本会产生输出漂移，门禁会误报。
- **命名检查**：`.clang-tidy` 里的 `readability-identifier-naming` 把 §3 的规则变成可执行检查。
- **管辖范围**：clang-tidy 只跑可移植代码与用户态代码。含 WDK 头的内核翻译单元 clang 解析不了，那部分靠评审。
- **禁止对仓库整体跑 `clang-tidy --fix`**：`--fix` 的实际改写范围不受 `--header-filter` 约束——它会把 filter 之外、只要被当前翻译单元包含到的头文件一并改掉。需要自动修时，先看诊断清单位置再逐个文件处理，或者直接手工改。
- **include 顺序有语义的地方用 `// clang-format off` / `on` 包住**：WDK 要求 `<ndis.h>` 前先定义 `NDIS630`、`fwpmk.h`/`fwpsk.h` 需要 `INITGUID`，而 clang-format 会按分类重排 include，两者直接冲突。
- **文档语言**：本项目文档以中文书写。

### 8.2 宿主门禁清单

宿主侧唯一入口是 `tools/check.sh`：

| 档位 | 步骤 | 预算 |
|---|---|---|
| `--fast`（默认） | 格式、有效行、cppcheck、禁止模式 | < 5 s |
| `--full` | `--fast` + 配置 `build/check` + clang-tidy 逐 TU + 构建并运行 `wf_check` | < 3 min |
| `--staged` | 只检查暂存区（索引）里的格式与禁止模式 | < 2 s |

`--fast --staged` 是提交前钩子的组合：格式与禁止模式看索引，其余快速检查照常看工作区。`--staged` 读的是 `git show :<path>` 的 blob，不是工作区文件。格式与禁止模式共用同一份 C++ 扩展名列表（`.c`/`.cc`/`.cpp`/`.cxx`/`.h`/`.hh`/`.hpp`/`.hxx`/`.inl`）——只在一侧加扩展名会让钩子漏文件。退出码只有三种：`0` 通过、`1` 有发现、`2` 环境或参数问题（检查器自身报错也算 2）。

cppcheck 与格式、禁止模式共用同一份扩展名列表，但只扫 `src/`——`tests/host` 由宿主编译器的 `-Wall -Werror` 覆盖，这是有意的范围差异。

### 8.3 禁止模式检查

`tools/lint/forbidden.py` 读 `tools/lint/forbidden-rules.txt`，行格式 `规则ID <TAB> glob <TAB> 正则 <TAB> 说明`。检查范围是 `src/driver/**` 与 `src/common/**` 的 C++ 源文件（扩展名列表与 `tools/check.sh` 共用：`.c`/`.cc`/`.cpp`/`.cxx`/`.h`/`.hh`/`.hpp`/`.hxx`/`.inl`）；匹配前剥离注释与字符串，所以注释与字符串字面量里的字样不会命中。

| 规则 ID | 模式 | 依据 |
|---|---|---|
| `stl` | 容器与字符串 blocklist：`\bstd::(…)\b`。覆盖 `array`、`basic_string`、`basic_string_view`、`deque`、`flat_map`/`flat_set` 等 `flat_*`、`forward_list`、`function`、`function_ref`、`list`、`map`/`multimap`/`multiset`/`set`、`mdspan`、`priority_queue`、`queue`、`shared_ptr`/`unique_ptr`/`weak_ptr`、`span`、`stack`、`string`/`wstring`/`u8string`/`u16string`/`u32string` 及各自的 `_view`、`unordered_*`、`vector`。这里只是摘要，完整正则见 `tools/lint/forbidden-rules.txt` | §5.1 标准库容器与字符串 |
| `exceptions` | `\b(throw\|try\|catch)\b` | §5.1 |
| `rtti` | `\b(dynamic_cast\|typeid)\b` | §5.1 |
| `heap` | **两个模式合列**（规则文件里是两行）：`(?P<allowed>operator\s+new\b\|<new>\|new\s*\((?!\s*(?:std::)?nothrow)(?!\s*(?:std::)?align_val_t))\|(?P<hit>\bnew\b)` 与 `(?P<allowed>=\s*delete\b\|operator\s+delete\b)\|(?P<hit>\bdelete\b)` | §5.1（排除项见下） |

`stl` 是 blocklist 不是白名单：它只拦已知依赖堆分配或异常的标准库设施，不碰 §5.2 允许的 `<cstddef>`/`<cstdint>`/`<limits>`/`<type_traits>`/`<utility>`/`<bit>`。新增被禁的设施时，先改规则文件再改本表。

`heap` 的两条正则用命名组 `hit`：只有 `hit` 参与匹配才算命中。`allowed` 分支排除 `<new>` 头、placement new、`operator new`/`operator delete` 声明与 `= delete` 声明——其中 `new (` 只在 placement new 时放行，`new (std::nothrow)`、`new (nothrow)`、`new (align_val_t{…})`、`new (std::align_val_t{…})` 仍算分配、必须报（§5.2 只允许用 `<new>` 做 placement new）。`delete *p`、`delete ::p`、`delete &x`、`delete[]` 仍然算命中。显式调用 `operator new(...)` / `operator delete(...)` 是罕见写法，不在正则里区分，由评审覆盖。

**已知局限（行内正则有边界）**：

- 跨 token 空白不覆盖：`std :: vector`、`std::` 换行 `vector`、`std/**/::vector` 都不会命中；
- 跨行写法不覆盖：placement new 的 `new` 与 `(` 分在两行、`=` 与 `delete` 分在两行；
- 这些只有评审能可靠识别，按 §5.1 逐条看。

**豁免语法**：在命中行或它的上一行写

```cpp
// WF-LINT-ALLOW(stl): 这里要复用共享头里的容器类型，理由写在这里
std::vector<int> values;
```

- 规则 ID 必须存在；一行里要豁免多条规则时用逗号分隔（`WF-LINT-ALLOW(stl, heap): …`）。
- **理由不能为空**：`// WF-LINT-ALLOW(stl):` 是无效豁免，检查会报 `[WF-LINT-ALLOW]`，命中照旧算发现。
- 豁免只认**原始行**里的注释；被字符串包住的字样不算。
- 豁免是逐行、逐规则的，没有文件级或目录级开关。

命中输出 `文件:行: [规则ID] 说明`，退出码 1。新增规则要同时改规则文件与本节的表格。

### 8.4 §5.1 与自动检查的对应

| §5.1 条目 | 执行方式 |
|---|---|
| `throw` / `try` / `catch` | 自动：`exceptions` |
| `dynamic_cast` / `typeid` | 自动：`rtti` |
| 标准库容器与字符串（`std::vector`、`std::string`、`std::map` …） | 自动：`stl` |
| 标准库智能指针与 `std::function`（§5.1「依赖堆分配与异常」口径的延伸） | 自动：`stl`（同一规则 ID，在规则文件里占同一行） |
| `new` / `delete` | 自动：`heap`（`<new>` 头、placement new、`operator new/delete`、`= delete` 除外；`nothrow`/`align_val_t` 的 `new (…)` 不算 placement new） |
| 全局或静态对象的动态初始化 | 评审：正则分不清定义与声明，也看不出构造时机 |
| 函数内静态局部变量 | 评审：同上，`static` 在合法位置也会出现 |
| `__try` / `__except` 与"持有析构函数的对象"同处一个作用域 | 评审：需要作用域与析构可见性分析 |
| `DISPATCH_LEVEL` 及以上的浮点运算 | 评审：需要 IRQL 传播分析 |

§5.1 共 8 条：4 条自动（本节按规则拆成 5 行——`stl` 拆成容器与字符串、智能指针两行）+ 4 条评审。自动项由门禁拦下；评审项没有自动检查，按 §5.1 逐条看。规则文件里多出本表的条目，就是文档没写清的规则，必须补进本表。

### 8.5 提交前钩子

钩子只做一件事：`tools/check.sh --fast --staged`。

- 安装：`.githooks/install.sh`（写 **local** 的 `core.hooksPath=.githooks`，安装前 local 的值记在 `<git-dir>/wf-hooks-path.state`）。
- 卸载：`.githooks/install.sh --uninstall`（按记录复原 local 的值；原来 local 没设过就清空，原来有值就写回——原本就是 `.githooks` 也照原样写回）。
- 两个操作都是一条命令，只动本仓库 local 作用域的配置；global / system 里的 `core.hooksPath` 不读也不写。没有安装记录且 local 未设置时，卸载是无操作。
- 绕过：`git commit --no-verify` 只跳过钩子，`tools/check.sh --fast` 仍是最终判据。

---

## 9. 测试约定

### 9.1 用例的通过原因必须就是它的名字

一条用例因为**错误的原因**通过，比没有这条用例更糟——它让人以为那里有覆盖。

这个错误在本项目里已经出现过两次：

- `rejects_an_unaligned_buffer` 起初传 `data() + 1` 且 `size - 1`，真正拒绝它的是长度不符而不是对齐检查；
- `rejects_overlapping_segments` 的三个夹具分别被「池尾 NUL」与「网段记录合法性」拒掉，三条重叠检查全删后仍然全绿。

### 9.2 验收方式是变异测试

对每一条校验 / 分支，判据不是「用例通过」，而是：**把被它保护的代码删掉或改成恒假，这条用例必须失败。**

做不到就说明它没钉住。做不到的常见原因是夹具同时违反了多条约束——要构造「只违反这一条」的输入。

写用例时顺手记下它钉的是哪条检查，并在被测代码旁注明这条检查是否已被某个用例独占锁定。

### 9.3 内存安全类的检查需要越界检测来证明

判断越界的检查（长度下限、边界读取）在逻辑层往往观察不到——短缓冲会被更早的 magic 比较挡掉。这类检查要靠：

- 把输入放在 `mmap` 页尾、下一页 `PROT_NONE`，让缺失的检查直接段错误；
- 或用 ASan / UBSan 构建跑同一批用例。

只用「返回 false」断言，删掉检查后用例仍可能通过。

### 9.4 门禁脚本同样适用

接进一键流程的验证脚本，**必须是会失败的**。只打印不断言、永远 `exit 0` 的脚本比没有更糟：它让上层那句 `if ($LASTEXITCODE -ne 0) { throw }` 变成空门，给人一种有守门的错觉。

这个错误在本项目里也出现过：`edge-cases.ps1` 只打印池计数就退出，主脚本对它的判空；`build-and-test.ps1` 的 DACL 检查只输出 accesschk 结果不判定，而 DACL 正是发布阻断项。

要求：

- 每条守门断言都要有**反向验证**——故意破坏被守的那件事，确认脚本变红；
- 计数类断言要精确（`allocs == before + 1`），不要写成「有输出就算过」；
- 检查安全属性时，断言的是**内容**（不含 `Everyone`），不是「命令跑成功了」；
- **不要用求和断言覆盖多条路径**。`a + b == 2` 在「a 走两次、b 走零次」时同样成立，等于两条路径都没钉住。已踩过：E2E 断言 `permitted_redirect_state + permitted_self_process == 2`，把 self-PID 闸整个拆掉仍然绿。要么拆成两条各自精确的断言，要么只断言真正被走到的那条、并写明另一条由谁覆盖。

### 9.5 差分测试只能发现「两侧不一致」，发现不了「两侧一致地错」

跨语言差分测试（C# 产 blob、C++ 消费）是本项目锁 ABI 的主要手段，但它有一个结构性盲区：**任何单侧就能自洽的规范化，只要两边都做、且做的是同一件（哪怕错误的）事，差分就绿。**

实测过两个例子：把 C# 侧的 ASCII 小写折叠删掉，差分全绿——因为 C++ matcher 两边都折叠，掩盖了；把网段的「主机位清零」删掉，同理，因为 matcher 按前缀比较，主机位不参与。

所以分工是：

- **差分测试**负责两侧对同一输入的**决策一致性**（字段偏移、字节序、布局、索引语义）；
- **单侧测试**负责**规范化不变量**（小写、主机位清零、去重、编码），因为这类东西只在一侧成立才有意义。

写差分测试时，别把它当成"万能门"。同时保留单侧用例钉住规范化，并在差分用例里避免"两侧都会补偿"的写法。

### 9.6 系统池标签表只反映「出现过」的标签

`NtQuerySystemInformation(SystemPoolTagInformation)` 的条目在**第一次分配时创建**，本次启动内即使
计数归零也仍然保留（实测：驱动卸载后 `WfPr` 仍显示 `39/39/0`）。用池标签做分配/释放断言时：

- **基线为 `null` 表示「还没有任何分配」**，按零参与差值，不是「读不到」，更不是失败；
- 窗口结束时仍然读不到，才说明条目在测量期间被清掉（计数不可判定），要显式报错；
- 差值断言要跨同一个条目：先减基线再加窗口内分配次数，别把「条目重建后计数从 0 起算」当成泄漏。

已经按这个语义落在 `tools/wfprobe/RuleSuite.cs` 与 `PoolTags.Zero`：首次运行（刚开机/刚装驱动）
不再偶发失败，`分配次数 / 释放次数 / outstanding = 0` 三条断言强度不变。
