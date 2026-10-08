# CLI 文本快照比对报告（E2-d：`Cli/CommandLine.cs` 解析器合一）

本报告是 E2-d 的判据文件：`client`/`target` 两个 verb 的 `--help`、根用法、以及**全部错误路径**在
改写前后的逐字文本。改写前的一棵树记在 `before/`，改写后记在 `after/`，比对是
`diff -r`（逐字节，含退出码与空白）。**结论：28 条命令里 8 个文件不同，全部是 `INTENTIONAL.md`
登记的那一句话；其余 77 个文件逐字节相同。**

---

## 0. 结论一句话

用**同一个采集脚本**（`benchmarks/WinForward.E2E/scripts/cli-snapshots.py`，28 条命令 ×
{退出码, stdout, stderr} = 每树 85 个文件）在**改写前的已发布二进制**（HEAD `1032bc7`）与
**改写后的已发布二进制**上各采一次：`diff -r --brief` 只报 8 个 `.stdout` 文件不同，
差异内容是 `target --help` 结语的同一处"一行改两行"（`Exits 0 on a clean shutdown, 2 on a usage error.`
→ `Exits 0 on a clean shutdown, 1 on a runtime error, 2 on a / usage error.`）；全部退出码、
全部 stderr、`client` 的全部文本、根用法文本、`index.json` 全部逐字节相同。

---

## 1. 采集方式与二进制身份

| 项 | 值 |
|---|---|
| 采集器 | `benchmarks/WinForward.E2E/scripts/cli-snapshots.py`（`<binary> <out-dir>`） |
| 工作目录 | 仓库根（`--plan` 用仓库相对路径，见下） |
| 每条命令记录 | `<nn>-<name>.exit`（一行十进制）、`.stdout`、`.stderr`（原始字节，不归一化空白） |
| 命令清单 | `index.json`（`name` / `stem` / `argv`，机器可读，供 xunit 快照测试回放） |
| 二进制身份（改写前） | `/tmp/e2d/before-linux`，`WinForward.E2E.dll` sha256 `f262a8a7d3e6afaf5f154ff3eec5bb27467f49b87e291a4cdc2d9a5967fe4902` |
| 二进制身份（改写后） | `/tmp/wf-bench/pub/linux`，`WinForward.E2E.dll` sha256 `8bca76db14cfda57b2a222dfbfc11649f68846263704276b9e0f6d21527f795f` |
| 两个二进制的关系 | 同一棵树、`WF_PUB` 两次发布；改写前的发布在 HEAD 上，改写后的发布同时是 `E2d-vs-run1.md` 里那两次等价性运行的二进制 |
| 发布脚本 | `benchmarks/WinForward.E2E/scripts/publish.sh`（linux-x64 框架依赖） |

`after/` 只采过一次"改写后"的树：第 6 条门禁（inspectcode）在第一次发布后报出一条
`ConvertIfStatementToReturnStatement`（`TargetOptions.TryCreate` 的 `if (!X) return false;`，
HINT 级），改成短路返回后**重新发布、重新采集 `after/`**，两棵树差集不变（仍是下面那 8 个文件、
同一句话）。即 `after/` 与等价性运行来自同一个已发布二进制。

两棵树的 apphost（`WinForward.E2E`，无扩展名）sha256 相同（`35802188398c3a00…`），所以**二进制身份
以 `WinForward.E2E.dll` 为准**——apphost 是固定模板，区分不出两次构建。

`--out` 用仓库根的 `.cli-snapshot-out`（不存在）。采集器在结尾断言它**没有**被任何一条命令创建：
清单里的每条命令都在解析器或 plan 读取处结束，两者都在"建目录/开 socket"之前。这条断言是
快照测试能在测试宿主里回放 `Program.Main` 的前提。

---

## 2. 快照清单（28 条命令）

`argv` 一列是二进制名之后的参数；stdout 一列写明该命令往 stdout 打的是什么。

| # | 命令（`WinForward.E2E` 之后的参数） | 退出码 | stdout | stderr |
|---|---|---|---|---|
| 00 | `client --help` | 0 | client help | （空） |
| 01 | `` | 2 | WinForward.E2E usage | （空） |
| 02 | `--help` | 0 | WinForward.E2E usage | （空） |
| 03 | `bogus` | 2 | WinForward.E2E usage | `e2e: unknown role 'bogus'.` |
| 04 | `target --help` | 0 | target help | （空） |
| 05 | `target --nope` | 2 | target help | `e2e target: unknown argument '--nope'` |
| 06 | `target --tcp-port` | 2 | target help | `e2e target: missing value for '--tcp-port'` |
| 07 | `target --tcp-port abc` | 2 | target help | `e2e target: 'abc' is not a port number` |
| 08 | `target --tcp-port 65536 --dns-port 53` | 2 | target help | `e2e target: ports must be in the range 1..65535` |
| 09 | `target --tcp-port 40010 --dns-port 40010` | 2 | target help | `e2e target: the dns port must differ from the tcp and udp ports: --dns-port…` |
| 10 | `target --udp-port 40010 --dns-port 40010` | 2 | target help | 同 09（撞 udp，选项名不同） |
| 11 | `target --dns-alt-port 30010` | 2 | target help | `e2e target: the additional dns port must differ from the tcp, udp and dns p…` |
| 12 | `target --bind localhost` | 2 | （空） | `e2e target: 'localhost' is not a valid IP address.` |
| 13 | `client --target 127.0.0.1 --out .cli-snapshot-out --nope` | 2 | client help | `e2e client: unknown argument '--nope'` |
| 14 | `client … --plan`（结尾无值） | 2 | client help | `e2e client: missing value for '--plan'` |
| 15 | `client … --plan=` | 2 | client help | `e2e client: --plan needs a path; omit the option to run the built-in plan` |
| 16 | `client … --plan ""` | 2 | client help | 同 15 |
| 17 | `client … --sampler-process=` | 2 | client help | `e2e client: --sampler-process needs a process name; a process is matched by…` |
| 18 | `client … --tcp-port abc` | 2 | client help | `e2e client: 'abc' is not a port number in 1..65535` |
| 19 | `client … --tcp-port 65536` | 2 | client help | `e2e client: '65536' is not a port number in 1..65535` |
| 20 | `client … --label --out x` | 2 | client help | `e2e client: '--label' value '--out' starts with '-'; a value that looks lik…` |
| 21 | `client --out .cli-snapshot-out` | 2 | client help | `e2e client: --target is required` |
| 22 | `client --target 127.0.0.1` | 2 | client help | `e2e client: --out is required` |
| 23 | `client … --plan tests/…/plans/beyond-int-window.json` | 2 | （空） | `e2e client: arm 'LATBEYONDINT' (kind 'latency'): 'window' is 3000000000, wh…` |
| 24 | `client … --plan tests/…/plans/unknown-key.json` | 2 | （空） | `e2e client: arm 'LOSS' (kind 'loss'): unknown key 'ratePerSeconds' (expecte…` |
| 25 | `client … --plan tests/…/plans/fractional-window.json` | 2 | （空） | `e2e client: arm 'LATFRACTION' (kind 'latency'): 'window' is 100.5, which is…` |
| 26 | `client … --plan tests/…/plans/colliding-file-names.json` | 2 | （空） | `e2e client: arm names 'A/B' and 'A_B' both map to the output file 'A_B.jsonl'` |
| 27 | `client … --plan tests/…/plans/arm-name-too-long.json` | 2 | （空） | `e2e client: arm 'AAAA…/////' maps to a 270-character file name 'AAAA…', above the 128-character limit` |

（`tests/…` = `tests/WinForward.E2E.Tests/Fixtures/plans`，plan 加载错误用测试工程的 Tier 0 fixture，
仓库相对路径，任何机器上同一份文件。省略号只出现在本表的摘要里，快照文件里是完整文本。）

**两个 verb 的错误通道不同，这一点是被快照冻住的**：解析期（选项名/取值/必填）失败时
`Program.cs` 打印 `e2e <verb>: <error>`（stderr）**并**打印该 verb 的 help（stdout）；plan 读取期
（23–27）与 `target --bind` 的地址解析（12）只打印 stderr，不打 help。合并不改变这条分界：
共享的 `CommandLine.TryParse` 只负责走参数，`Program.cs` 的两条分支没动。

---

## 3. 逐字比对：before ↔ after

```console
$ diff -r --brief research/cli-snapshots/before research/cli-snapshots/after
Files before/04-target-help.stdout and after/04-target-help.stdout differ
Files before/05-target-unknown-option.stdout and after/05-target-unknown-option.stdout differ
Files before/06-target-missing-value.stdout and after/06-target-missing-value.stdout differ
Files before/07-target-port-not-a-number.stdout and after/07-target-port-not-a-number.stdout differ
Files before/08-target-port-out-of-range.stdout and after/08-target-port-out-of-range.stdout differ
Files before/09-target-dns-collides-with-tcp.stdout and after/09-target-dns-collides-with-tcp.stdout differ
Files before/10-target-dns-collides-with-udp.stdout and after/10-target-dns-collides-with-udp.stdout differ
Files before/11-target-dns-alt-collides.stdout and after/11-target-dns-alt-collides.stdout differ
```

8 个文件，全部是 stdout；每个文件的差异是同一处（`04` 的 hunk，其余 7 个逐字相同、只是文件名不同）：

```diff
--- before/04-target-help.stdout
+++ after/04-target-help.stdout
@@ -8,4 +8,5 @@
   --label <name>       Run or row identity copied into every ledger record (default empty)
   --ledger <path>      JSONL ledger output path (default target-ledger.jsonl)
 
-Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 2 on a usage error.
+Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 1 on a runtime error, 2 on a
+usage error.
```

把两棵树的全部"变更行"按内容归类，只有三种（8 次各一）：

```console
$ diff -ru before after | rg '^[+-][^+-]' | sort | uniq -c
      8 +usage error.
      8 -Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 2 on a usage error.
      8 +Runs until Ctrl+C or SIGTERM. Exits 0 on a clean shutdown, 1 on a runtime error, 2 on a
```

**逐字节相同的 77 个文件**：`index.json`（命令清单）、全部 28 个 `.exit`、全部 28 个 `.stderr`，
以及 20 个不含 target help 的 `.stdout`（`client` 的全部文本 + 根用法 + 计划加载错误）。

`target` 的 help 出现在 8 个文件里，是因为 usage 错误后 `Program.cs:73` 会打印它——这是既有行为
（改写前也一样），不是这次多出来的。

---

## 4. 有意变更

登记在 `INTENTIONAL.md`，只有一条：`target --help` 补上 E1 引入的退出码 1 的说明。测试侧的落地是
`tests/WinForward.E2E.Tests/CliSnapshotTests.cs`：

- `EveryRecordedCommandStillPrintsItsRecordedText`：回放 28 条 `argv`，逐条比对退出码 + stdout + stderr。
  含 target help 的那些条目（由**记录文本**判定，不是硬编码清单）与 `after/` 比，其余与 `before/` 比。
- `TheRegisteredTargetHelpSentenceIsTheOnlyDifferenceBetweenTheTrees`：对每一条断言
  `after == before.Replace(旧句, 新句)`（旧句不在的文件上 `Replace` 是恒等，即"其余文件逐字相同"
  也被这条断言覆盖），并单独断言 `target --help` 确实变成了新句。

反证（实跑并还原，记录在 `E2d-vs-run1.md` §5）：删掉新句 → 回放测试红（`Expected: …1 on a runtime error…` /
`Actual: …2 on a usage error.`）；把 `missing value for` 改成 `no value for` → 回放测试红且两条
行为断言同时红。

---

## 5. 复跑方式

```console
# 1. 改写前的树（HEAD）发布到一个独立目录，采 before/
$ cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e2d/pub scripts/publish.sh
$ cd ../.. && python3 benchmarks/WinForward.E2E/scripts/cli-snapshots.py \
      /tmp/e2d/pub/linux/WinForward.E2E research/cli-snapshots/before

# 2. 改写后的树同样发布 + 采 after/（脚本、命令清单都不变）
$ cd benchmarks/WinForward.E2E && WF_PUB=/tmp/e2d/pub scripts/publish.sh
$ cd ../.. && python3 benchmarks/WinForward.E2E/scripts/cli-snapshots.py \
      /tmp/e2d/pub/linux/WinForward.E2E research/cli-snapshots/after

# 3. 判据
$ diff -r research/cli-snapshots/before research/cli-snapshots/after   # 只允许 04..11 的 stdout

# 4. 测试层（回放生产解析器，不需要已发布二进制）
$ dotnet test tests/WinForward.E2E.Tests -c Release --filter 'FullyQualifiedName~CliSnapshotTests'
```

（`research/` = `.trellis/tasks/10-07-e2e-harness-refactor/research/`；快照测试通过
`RepoPaths.CliSnapshotsDirectory` 定位，任务归档后会跟随到 `.trellis/tasks/archive/<month>/` 下。）

> **哈希口径**：本文件记录的是**实现轮**冻结树的读数（`WinForward.E2E.dll` = `8bca76db…`）。
> check 轮的两处**文字**修复（`CommandLine.cs` 的 remarks、README Layout 一行）使发布哈希变为 `8202869d…`；
> 用户可见文本零变化（修复后二进制重采的 28 条快照与 `after/` 逐字节相同），且门禁与等价性判定
> 已在 `8202869d…` 上整体重跑 —— 见 `E2d-check.md` §9.2。
