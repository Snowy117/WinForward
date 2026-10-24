# Host 规则数组与进程选择器 parity —— 实施计划

## 0. 开工前

- [ ] 从 `main` 建工作分支 `feat/host-rule-arrays-process-match`（当前 `experiment/connected-udp-ale-nat`
      上的 NO-GO 实验不应混入本任务）；`task.py set-base-branch main`。
- [ ] 确认工作区干净（本任务目录之外没有未提交改动）。
- [ ] 基线：`tools/check.sh --full` 与 `tools/userland/verify-gates.sh` 先绿，记录耗时与结果。

## 1. 共享 ABI（`src/common`）

- [ ] `rule_blob.h`
  - [ ] 版本 2、header 56 字节、rule 32 字节、process_selector 8 字节、port_range 4 字节；
        全部 `static_assert`（尺寸、对齐、`offsetof`）。
  - [ ] 新增协议 / 地址族掩码常量、selector kind 常量；保留 `network_prefix` 20 字节不变。
  - [ ] `blob_view` 增加 selector / port 视图；`rule_pool_index_any` 只保留给「未命中规则序号」。
  - [ ] `try_open_blob` 实现 §3.5 的全部校验；把 UTF-8 / 长度 / 控制字符校验移进这里。
  - [ ] 注释更新为 v2 布局与语义。
- [ ] `matcher.h`
  - [ ] 进程选择器：文件名 / 路径（相等或前缀 + `\`），数组 OR。
  - [ ] 协议 / 地址族掩码；端口 / 网段数组 OR；`count == 0` 表示任意。
  - [ ] 保持无分配、无异常、DISPATCH_LEVEL 可用。

## 2. 宿主 C++ 测试

- [ ] `tests/host/rule_blob_builder.h` 改成 v2 构造器（selector / port 池、数组规则）。
- [ ] `tests/host/rule_blob_test.cpp`
  - [ ] 逐条新校验的负向夹具：只违反一条检查，删掉该检查后用例必须失败。
  - [ ] 段重叠、对齐、越界、count/first 不一致、掩码越界、kind/is_directory 非法、
        文件名含分隔符、路径不以 `\` 开头 / 以 `\` 结尾、族与网段不一致、非法 UTF-8 / 超长、
        端口 start > end。
  - [ ] ASan / 页尾越界检测沿用现有做法。
- [ ] `tests/host/matcher_test.cpp`
  - [ ] 文件名命中 / 不命中；大小写；`tools` vs `tools2`；目录直接子文件与深层后代；
        精确路径；进程路径缺失。
  - [ ] 集合 OR / AND 矩阵：多进程、多网段、多端口、多协议、多族。
  - [ ] 首个命中与 `rule_index`。
- [ ] `tests/host/differential_test.cpp` / `differential_fixture.h`
  - [ ] 清单格式不变；确认 v2 blob 能打开并消费。
  - [ ] 如清单里断言了 header_size=40，改为 56。

## 3. 用户态 ABI 镜像

- [ ] `RuleBlobLayout.cs`：v2 常量（版本、header/rule/selector/port 尺寸、池长）。
- [ ] `RuleBlobHeader.cs`（56）、`RuleBlobRule.cs`（32）。
- [ ] 新增 `RuleBlobProcessSelector.cs`（8）、`RuleBlobPortRange.cs`（4）。
- [ ] `BlobReader.cs`：`Selector`、`PortRange`、按字符串偏移读进程串。
- [ ] `LayoutTests.cs`：尺寸、逐字段偏移、`Pack = 4`、与 `rule_blob.h` 数值一致。
- [ ] `PolicyLimits.cs`：新增 `MaxBlockFilters = 128`、`RuleBlobMaxSize = 64 * 1024`；
      在 `AbiConsistencyTests` 里从 `probe_driver.h` / `probe_ioctl.h` 读同名常量并比较。

## 4. 用户态模型与编译

- [ ] `ProcessSelector.cs`
  - [ ] `Kind`（FileName / Path）+ `Value`；`Parse` trim、控制字符 / UTF-8 校验、`/`→`\`；
        文件名型拒绝分隔符；路径型保留原样交给归一化。
- [ ] `IProcessPathNormalizer.cs` + 新增 `ProcessPathResolution.cs`
  - [ ] `Normalize(string path) → ProcessPathResolution? { DevicePath, IsDirectory }`。
- [ ] 新增 Windows 实现（替换 `FwpmAppIdNormalizer` / `WindowsAppId`）
  - [ ] `Path.GetFullPath` → `\\?\` / `\\?\UNC\` → `CreateFileW(FILE_FLAG_BACKUP_SEMANTICS)` →
        `GetFinalPathNameByHandleW(FILE_NAME_NORMALIZED | VOLUME_NAME_NT)` →
        `GetFileInformationByHandle` 判目录 → ASCII 小写折叠。
  - [ ] 非 Windows 抛 `PlatformNotSupportedException`；测试注入解析器。
  - [ ] 保留 `AsciiLowercaseNormalizer` 的小写折叠能力（改为内部 helper 或静态方法）。
- [ ] `PolicyRule.cs`：五个选择器改为 `IReadOnlyList<T>?`（null = 任意），删掉单值属性。
- [ ] `CheckCommand.cs` / `RunCommand.cs`：改用新的 Windows 解析器；无 process 规则时用一个
      明确拒绝调用的空实现（编译器不会调用它），不要静默返回错误路径。
- [ ] `RuleBlobCompiler.cs`
  - [ ] v2 编码：进程串池（去重）+ selector 池 + 网段池 + 端口池；规则区间连续。
  - [ ] 路径解析、文件名 / 路径区分、目录标记、ASCII 小写、511 字节上限、控制字符。
  - [ ] 集合非空、族与网段一致、端口排序合并、协议 / 族掩码。
  - [ ] block 表达边界：只允许路径型 + `IsDirectory == false`。
  - [ ] block filter 预算（§6 公式）与 64 KiB 总长上限。
  - [ ] `CompileUnchecked` 只跳过顺序 / block 边界 / 预算，布局仍合法。
- [ ] `NetworkPoolBuilder.cs`：改为按规则追加记录（不再跨规则去重）。
- [ ] 新增端口池构造（可直接在编译器内用 `List<RuleBlobPortRange>`）。
- [ ] `RuleOrderValidator.cs`
  - [ ] 集合掩码 / 数组端口 / 数组网段 / 前缀进程重叠，算法与驱动逐条对应。
  - [ ] 错误消息带两条规则序号与条件摘要（数组用逗号列出）。

## 5. 配置层

- [ ] `PolicyConfigurationDocument.cs`
  - [ ] `process` / `remoteNetwork` / `remotePorts` / `protocol` / `family` 接受单值或数组。
  - [ ] 空数组、空元素、非字符串元素、未知值带 JSON 路径拒绝。
  - [ ] 端口：`*` 单独出现 = 任意；0 拒绝；排序 + 合并相邻 / 重叠区间。
  - [ ] 网段 / 协议 / 族去重；`any` 处理与文档一致。
- [ ] 更新 `src/userland/README.md` 的配置示例与说明。

## 6. 差分与夹具

- [ ] `Differential.cs` / `PriorityCases.cs` / `NetworkCases.cs` / `TransportCases.cs`
  - [ ] 适配新的 `PolicyRule` 集合模型。
  - [ ] 新增用例：文件名、精确路径、目录前缀、数组 OR/AND、混合族、多端口、多进程、
        一条规则多值仍只占一个 rule_index。
  - [ ] 更新 `DifferentialTests` 的期望用例数 / 连接数。
- [ ] `DifferentialFixtureWriter` 与 `tests/host/differential_fixture.h`：格式不变；
      如需新增字段，先同时改两侧并升级 manifest 版本。
- [ ] `tools/wfprobe/BlobBuilder.cs` / `Protocol.cs`
  - [ ] v2 构造器、`TestRule` 数组 / selector kind / is_directory、预算与顺序校验镜像。
- [ ] `tools/wfprobe/RuleSuite.cs`
  - [ ] 新增数组、文件名、目录前缀、`tools` vs `tools2`、block 精确路径数组、
        block + 文件名 / 目录负向用例；规则数断言与配置规则数相等。
- [ ] `tools/userland/verify-gates.sh`
  - [ ] 为新增编译期检查补变异用例（去掉检查 → 对应测试必须变红）。

## 7. 驱动

- [ ] `src/driver/rule_table.cpp`
  - [ ] 删掉本地 UTF-8 选择器校验（已移入 `try_open_blob`）；保留 mutation 标记或迁移到新位置。
  - [ ] `rule_order_valid` 按集合 / 进程前缀语义重写，与 C# 一致。
  - [ ] 增加 block 表达边界检查（block + 文件名 / 目录 → 整块拒绝并 DbgPrint 规则序号）。
  - [ ] 保留 fallback=block 冲突检查。
- [ ] `src/driver/block_filters.cpp`
  - [ ] 按 §6 展开数组；显式协议条件；网段 / 端口按层过滤；app-id 只接受精确文件路径。
  - [ ] 超出 `max_block_filters` 时删除已建 filter 并返回 `STATUS_INVALID_PARAMETER`。
- [ ] `src/driver/probe_driver.h` / `src/driver/README.md`：注释与文档同步 v2。
- [ ] `tools/windows/mutation.ps1`：`selector-utf8-guard` 的标记随校验移入 `rule_blob.h`，
      更新变异脚本的文件路径与新标记；为新顺序 / block 边界检查按需补变异项。
- [ ] `probe_classify.cpp` / `process_path.cpp`：不需要改语义；确认 `connection_view` 字段与
      matcher 新签名一致。

## 8. 文档与 spec

- [ ] `wfp-architecture.md` §5（blob v2）与 §7.10（三种进程选择器、目录解析改句柄法、block 边界）。
- [ ] `src/userland/README.md`、`src/driver/README.md`、`src/common/rule_blob.h` 注释。
- [ ] Phase 3.3：新增 `.trellis/spec/backend/host-rule-blob.md`（v2 布局、语义、block 边界、
      两侧校验清单），并在 `index.md` 挂链接。

## 9. 验证顺序

1. [ ] 宿主单测与 C++ 构建：
       `nix develop -c dotnet run --project src/userland/WinForward.Userland.Tests -- test`
2. [ ] 全量宿主门禁：`nix develop -c tools/check.sh --full`
       （含 clang-tidy、`wf_check` → C# 测试 + 生成夹具 + ctest + 差分）。
3. [ ] 变异门禁：`nix develop -c tools/userland/verify-gates.sh`。
4. [ ] Linux 侧启动 echo target（`tools/linux/tcp_echo.py`，端口 40000–41000）。
5. [ ] 同步工作树到 VM（zip/tar 排除 `build/`、`bin/`、`obj/`、`.git`、`local/`），
       解压到 `C:\workspace\WinForward.Kernel`；确认 VM 上的 `dotnet` 可用。
6. [ ] VM 门禁：`pwsh -File tools/windows/gate.ps1 -Root C:\workspace\WinForward.Kernel -Remote 192.168.100.4:<port>`
       - 驱动构建 0 warning、签名 Valid、驱动 RUNNING；
       - `appid`、`rules`（含新用例）、`udp-rules`、`forwarded-rules`、`selftest`、`gates` 全绿；
       - 记录驱动 SHA-256。
7. [ ] 如失败：先修根因，不跳过检查；必要时用 `wfprobe <suite>` 单独复现。
8. [ ] 记录验证日志到任务 `verification.md`（或 research/ 下的验证记录）。

## 10. 提交

- [ ] 按 Phase 3.4 的批次计划提交：共享 ABI + 宿主测试、用户态模型 / 编译 / 配置、差分与
      wfprobe、驱动、文档与 spec。
- [ ] 不 amend、不 push、不跳过钩子。
- [ ] 提交后 `/trellis:finish-work` 归档任务并写 session。

## 风险文件与回滚点

| 文件 / 批次 | 风险 | 回滚 |
|---|---|---|
| `src/common/rule_blob.h` | ABI 破坏性变更 | 与 C# 镜像、驱动、wfprobe 同批回退 |
| `RuleBlobCompiler.cs` + 模型 | 现有配置编译失败 | 配置层保留单值兼容；回退整批 |
| `rule_order_valid` / `RuleOrderValidator` | 误拒或漏拒 | 两侧同时回退；用矩阵用例锁定 |
| `block_filters.cpp` | filter 数量 / 条件错误 | 回退整批；旧表在失败路径保持不变 |
| Windows 路径归一化 | 目录 / 长路径解析 | 注入解析器保留宿主测试；生产实现单独回退 |

## 已知工作边界

- 需要 Windows VM 与外网目标才能跑完整驱动门禁；Linux 侧只能覆盖可移植核心与 C#。
- 路径选择器要求编译期路径存在；不存在的目录会编译失败（与文件选择器一致，且更可诊断）。
- block 不支持文件名 / 目录前缀；这是本任务明确保留的边界，sink 方案另开任务。
