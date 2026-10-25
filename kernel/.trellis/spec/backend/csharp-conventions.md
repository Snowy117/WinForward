# C# 编码规范（用户态）

> 适用范围：`src/userland/` 下的全部 C# 代码。
> 拟定：2026-10-20。

这份规范只覆盖 C# 侧。C++ 侧（含 `src/common`、`tests/host`）仍然遵守
[C++ 编码规范](./cpp-conventions.md)：**除宏之外全部 snake_case**。

两侧命名风格**有意不同**：C++ 按本仓库的规范，C# 按 .NET 生态的惯例。不要为了「统一」
去扭曲任何一边 —— 共享的是字节布局与语义，不是标识符拼写。

---

## 1. 工具链

- 目标框架固定 `net10.0`（`Directory.Build.props` 统一设置）。仓库 devShell 提供
  `dotnet-sdk_10`（`flake.nix`，pinned nixpkgs 上是 10.0.401），Windows VM 上是 SDK 11，
  两侧都能编 `net10.0`。升目标框架等于同时改两侧工具链，按破坏性变更处理。
- devShell 里同时设了 `DOTNET_CLI_TELEMETRY_OPTOUT=1` 与 `DOTNET_NOLOGO=1`，别在 CI 脚本里
  再重复一遍。
- 公共属性：`Nullable=enable`、`ImplicitUsings=enable`、`TreatWarningsAsErrors=true`、
  `Deterministic=true`。警告即错误，别用 `#pragma` 静音。
- 测试工程**不引 NuGet 包**。仓库的构建面只有 nix devShell 与 Windows VM，两者都不保证
  能访问 nuget.org（`src/driver/NuGet.config` 已经把源指到本地目录）。

---

## 2. 命名

| 符号 | 规则 | 例 |
|---|---|---|
| 命名空间 | PascalCase，点分层级 | `WinForward.Userland.Policy` |
| 类型（类 / 结构 / 记录 / 枚举 / 委托） | PascalCase | `RuleBlobCompiler`、`PortRange` |
| 接口 | `I` + PascalCase | `IRuleSource` |
| 方法 / 属性 / 事件 | PascalCase | `Compile`、`FallbackAction` |
| 公开字段（含 ABI 镜像结构） | PascalCase | `RuleBlobHeader.TotalSize` |
| 参数 / 局部变量 | camelCase | `fallbackAction`、`processPool` |
| 私有字段 | `_camelCase` | `_address`、`_offsets` |
| 常量 | PascalCase | `ManifestFileName` |
| 枚举成员 | PascalCase | `RuleAction.Redirect` |

C# 的 ABI 镜像结构用 `public` 字段（而不是属性）：`MemoryMarshal.Write` 只吃字段布局，
属性会改变结构体的可 blittable 性。字段名按上表 PascalCase，C++ 侧仍是 snake_case ——
命名规则只有一份权威表述在 [C++ 编码规范 §3](./cpp-conventions.md)，本表只列 C# 的目标形式，
不重复解释理由。字段级对照表见 `.trellis/tasks/10-20-wfp-csharp-blob/research/blob-contract.md`。

---

## 3. 文件与工程布局

- 一个文件一个公开类型，文件名与类型同名。
- 命名空间用文件级声明（`namespace X;`），不写块级嵌套。
- 工程按职责分：`WinForward.Userland`（产品逻辑，`Policy/` 命名空间）、
  `WinForward.Userland.Tests`（测试与夹具生成）。测试代码不混进产品工程。

---

## 4. 错误处理

| 场景 | 约定 |
|---|---|
| 模型构造（路径、CIDR、前缀长度） | 抛 `ArgumentException` / `ArgumentOutOfRangeException`，参数名写清楚 |
| 编译期语义错误（未知枚举值、端口区间颠倒、族不一致） | 抛 `PolicyCompileException`，消息必须带规则序号 |
| 内核侧 | 不涉及：内核只做整块接受或整块拒绝 |

用户态是唯一能给出可诊断信息的时刻，消息里要有「第几条规则、哪个字段、期望什么」。

不允许提供「忘了传就静默走默认实现」的重载：`RuleBlobCompiler.Compile` 只接受显式的
`IProcessPathNormalizer`。测试注入假映射（`TestAppId`），生产入口注入
`WindowsProcessPathNormalizer.ForWindows()` —— 静默降级（例如忘传就退回 DOS 路径）会让规则不生效
却不报错，这正是这一层要避免的事。路径型选择器走句柄法（`CreateFileW` +
`GetFinalPathNameByHandleW(VOLUME_NAME_NT)`）解析成 `ALE_APP_ID` 同形的设备路径，并要求路径
在编译期存在；文件名型不经过解析器，只折叠 ASCII 大小写。

---

## 5. 测试

- 测试驱动是工程内自带的零依赖 runner（`TestRunner` + `Check`），入口：

  ```bash
  dotnet run --project src/userland/WinForward.Userland.Tests -- test
  ```

  退出码即门禁结果；`dotnet run ... -- emit-fixtures [目录]` 只生成差分夹具。
- 断言消息必须同时给出**期望值与实际值**（`Check` 已经这么做了，别绕开它）。
- ABI 镜像结构必须有布局断言：`Marshal.SizeOf`、逐字段 `Marshal.OffsetOf`、
  `Unsafe.SizeOf` 一致、`Pack == 4`，数值直接照抄 `src/common/rule_blob.h`；
  `BlobAbiConsistencyTests` 再从源码文本把版本、尺寸、掩码与 511 / 64 KiB 两个上限
  与 `PolicyLimits`、`tools/wfprobe/Protocol.cs` 互锁。
- 测试的通过原因必须就是它的名字（见 [cpp-conventions.md §9](./cpp-conventions.md)）；
  计数类断言写精确值，不写「非空即过」。

---

## 6. 有效行

单文件有效行（去掉空行与纯注释行）**同样**不超过 500 行。超过就拆文件或提取共同结构；
确需超限要在文件头写明理由。
