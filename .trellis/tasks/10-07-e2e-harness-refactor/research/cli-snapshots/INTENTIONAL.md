# 有意变更登记（`research/cli-snapshots/`）

本目录下的 `before/` 与 `after/` 是同一份命令清单在**改写前后两个已发布二进制**上的逐字文本。
判据是"除本文件登记的条目外，两棵树逐字节相同"。本批（E2-d：`Cli/CommandLine.cs` 解析器合一）
登记的变更**只有一条**。

---

## 1. `target --help` 补上退出码 1 的说明（唯一的登记项）

| | |
|---|---|
| 位置 | `benchmarks/WinForward.E2E/Target/TargetRunner.cs` 的 `PrintHelp()` |
| 来源 | E1 引入：`target` 的 `SocketException` 与兜底 `Exception` 走 `ExitCodes.RuntimeError`（=1），`Program.cs:81-96` |
| 变更 | 结语由一句改两句（下方 diff） |
| 影响面 | 8 个快照文件：`04-target-help.stdout` 与 7 条 usage 错误的 stdout（`target` 在解析失败后打印 help，见 `Program.cs:73`） |
| 不影响 | 全部 28 条的退出码、全部 stderr、`client` 的全部文本、`index.json`、根用法文本 |

```diff
-Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 2 on a usage error.
+Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 1 on a runtime error, 2 on a
+usage error.
```

折行与 `client --help` 的同类结语一致（`ClientRunner.PrintHelp()` 的
"The client exits 0 when every requested arm completed, 1 when an arm failed, 2 on a / usage error."）。
该句在测试里的落地位置是 `tests/WinForward.E2E.Tests/CliSnapshotTests.cs` 的两个常量
（`TargetHelpBefore` / `TargetHelpAfter`）与
`TheRegisteredTargetHelpSentenceIsTheOnlyDifferenceBetweenTheTrees`。

---

## 2. 为什么只有这一条

E2-d 的判据是"**语义与错误文本逐字不变**"（`implement.md` 的 E2-d 节）。解析器合一是重构而非
行为变更：两个 verb 的已知选项集合、每个选项的拒绝条件与错误文本、`--help` 的两段文本、退出码
映射（0/1/2）都逐字保留，唯一被允许的差异就是本条登记项。

`after/` 树里没有第二条差异，比对证据见 `REPORT.md` 第 3 节（`diff -r` 的全部输出）。
