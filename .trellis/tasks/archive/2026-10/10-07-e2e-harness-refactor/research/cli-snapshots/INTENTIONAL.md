# 有意变更登记（`research/cli-snapshots/`）

本目录下的 `before/` 与 `after/` 是同一份命令清单在**改写前后两个已发布二进制**上的逐字文本。
判据是"除本文件登记的条目外，两棵树逐字节相同"。

- **E2-d**（`Cli/CommandLine.cs` 解析器合一）：登记 **1 条**变更（下表 §1）。
- **E3-d**（账本三字段 + `--udp-receivers`）：登记 **2 条**（§3 的 help 行 + §4 的三条新增 case）。
  两者都在 `after/` 一侧，`before/` 是 E2-d 之前那个二进制录的，不再重采。

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

---

## 3. E3-d：`target --help` 新增 `--udp-receivers` 一行

| | |
|---|---|
| 位置 | `benchmarks/WinForward.E2E/Target/TargetRunner.cs` 的 `PrintHelp()` |
| 来源 | E3-d 新增靶机选项 `--udp-receivers`（D19.2 ⑬ 的落点；默认值仍是 `TargetRunner.cs:21` 的原公式） |
| 变更 | `--udp-port` 之后插入两行（选项名 + 折行的说明） |
| 影响面 | 与 §1 相同的 8 个快照文件（`target` 的 usage 错误会打印 help） |
| 不影响 | 全部 31 条的退出码、全部 stderr、`client` 的全部文本、`index.json`、根用法文本 |

```diff
   --udp-port <n>       UDP echo listener port (default 30010)
+  --udp-receivers <n>  UDP receive loops each datagram listener runs (default: half the
+                       processors, clamped to 2..8)
   --dns-port <n>       DNS responder port, UDP and TCP (default 30053)
```

折行（第二行对齐描述列）与 `target --help` 结语在 §1 里的折行同形。测试侧的落地是
`tests/WinForward.E2E.Tests/CliSnapshotTests.cs` 的 `UdpPortHelpBefore`/`UdpPortHelpAfter`
常量、`WithRegisteredChanges`（两条登记项各一次 `Replace`）与
`TheRegisteredChangesAreTheOnlyDifferenceBetweenTheTrees`。

## 4. E3-d：新增三条 case（只存在于 `after/`）

| # | 命令 | 退出码 | stderr |
|---|---|---|---|
| 28 | `target --udp-receivers abc` | 2 | `e2e target: 'abc' is not a receive-loop count` |
| 29 | `target --udp-receivers 0` | 2 | `e2e target: the udp receive-loop count must be in the range 1..64` |
| 30 | `target --udp-receivers 65` | 2 | 同 29 |

**为什么 `before/` 里没有这三条**：`before/` 是 E2-d 之前那个二进制录的，它对
`--udp-receivers` 只会说 `unknown argument`；用旧二进制的输出当这三条的冻结文本，会把"这个选项
不存在"冻成契约。因此它们只进 `after/`（`28..30`，**追加在末尾**：stem 是 case 的身份，插入会
让已录的树错位），由 `CliSnapshotTests.TheCasesTheBeforeTreePredatesStillPrintTheirRecordedText`
逐条回放比对（退出码 + stdout + stderr）。

三条都是**拒绝路径**，所以清单仍然是"每条命令都在建目录/开 socket 之前结束"（采集器结尾的
`.cli-snapshot-out` 断言照旧成立）：`0` 与 `65` 走同一个区间拒绝，`abc` 走"不是十进制数"。
