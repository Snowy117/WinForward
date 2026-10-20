# WinForward.Userland —— 用户态策略编译

把有序规则表编译成 `src/common/rule_blob.h` 描述的内核规则 blob，并用**差分测试**锁住
跨语言 ABI：C# 侧产出 blob 与「连接 → 期望决策」表，C++ 侧用同一份 `try_open_blob` +
`match` 消费并逐条比对。

blob 格式一旦对不上，症状是规则静默不生效而不是报错，所以两侧共享的测试是这一层的
主要产出，不是附属品。

## 工程结构

| 路径 | 内容 |
|---|---|
| `WinForward.Userland/Policy/` | 规则模型、编译期校验、blob 编译器、ABI 镜像结构 |
| `WinForward.Userland.Tests/` | 零依赖测试驱动、布局断言、差分用例表与夹具生成 |
| `Directory.Build.props` | 公共属性：`net10.0`、nullable、warnings-as-errors |

测试工程刻意不引 xunit：仓库的构建面只有 nix devShell 与 Windows VM，两者都不保证能访问
NuGet。入口是 `dotnet run` 的 `test` 子命令，退出码即门禁结果。

C# 工具链由 devShell 提供（`flake.nix` 的 `dotnet-sdk_10`，目标框架 `net10.0`），不依赖宿主的
全局 dotnet。

## 构建与测试

一条命令（生成夹具 → 构建 → 全部 ctest，任一失败即失败）：

```bash
cmake -S . -B build -G Ninja
cmake --build build --target wf_check
```

分开跑也可以，但**顺序不能反**：

```bash
# C# 侧：单元测试 + 布局断言 + 差分夹具生成（32 组用例写入 build/diff-fixtures）
dotnet build src/userland/WinForward.Userland.slnx
dotnet run --project src/userland/WinForward.Userland.Tests -- test

# C++ 侧：读取夹具，逐条断言决策一致
cmake --build build
ctest --test-dir build --output-on-failure
```

两侧的夹具目录必须一致。C# 侧默认写 `<仓库根>/build/diff-fixtures`；C++ 侧默认读
`${CMAKE_BINARY_DIR}/diff-fixtures`，用标准构建目录 `build` 时两者相同。用环境变量
`WF_DIFF_FIXTURE_DIR` 可以同时改掉两侧的默认目录。

C++ 侧读不到夹具会直接失败（并打印生成命令），不会静默跳过；只改 C# 期望却不重跑生成时，
`ctest` 读的是旧夹具 —— 这正是 `wf_check` 存在的理由，它先把夹具重新生成一遍再跑全套。


## 有效行

单文件有效行（去掉空行与整行注释）不超过 500，检查脚本会真的失败：

```bash
python3 src/userland/scripts/check-effective-lines.py
```

## 与内核的边界

- 所有取舍在编译期做完：进程路径经 `FwpmAppIdNormalizer` 解析成 ALE_APP_ID 的设备路径、
  折叠 ASCII 小写、去重、NUL 结尾；网段主机位清零、20 字节定长去重；
  索引换算成池内字节偏移；
- 内核侧 `try_open_blob` 只解码与校验，`match` 只按数组顺序找首个命中，不解释用户意图。
- 兜底动作写在 blob 头部，与规则表一起原子替换。
