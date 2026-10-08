baseline binary sha256: apphost `/tmp/wf-bench/pub/linux/WinForward.E2E` = `35802188398c3a0074f3ae16078a1f8f3473f12e32622bdca765429e38da893d`（该文件是原生 apphost，**哈希不随代码变化**，两棵树发布结果相同）；受管代码 `WinForward.E2E.dll` = `2eb371625f7f055c594d1de3f77069a5be545cdc5ec9d7cdfaeade810fa51a58`（HEAD `d90707eb`，独立 worktree 重建验证，见 `00-state.md` 末节）

# A0 回归基线（E1-A）

本文件是 DD **D7 / D14.8** 要求的基线证据。基线期间（`00-state.md` 记录的 HEAD `d90707eb`）未写任何脚本、
未改任何被测代码；`git status --porcelain` 的完整快照见 [00-state.md](./00-state.md)。

## 1. 取得方式

```bash
benchmarks/WinForward.E2E/scripts/publish.sh            # 强制重建三份产物（不是 selftest 的“缺了才 publish”）
sha256sum /tmp/wf-bench/pub/linux/WinForward.E2E        # 35802188…893d
cd benchmarks/WinForward.E2E
scripts/selftest.sh scripts/plans/selftest-plan.json    # run1 → 退出码 0
scripts/selftest.sh scripts/plans/selftest-plan.json    # run2 → 退出码 0
```

两次运行共用同一个二进制（run2 结束后复算 sha256 仍为 `35802188…893d`）。每次运行结束后立即把
`/tmp/wf-bench/selftest/{out,ledger.jsonl,target.out}` 与完整脚本输出拷入 `run1/`、`run2/`：

| 产物 | 位置 |
|---|---|
| run1 的 `out/`（11 个 `<arm>.jsonl` + `run.json`）、`ledger.jsonl`、`target.out`、完整输出、退出码 | `run1/` |
| run2 同上 | `run2/` |
| git HEAD / status / sha256 / publish 日志 | `00-state.md` |
| 抖动带（机器可读，903 条逐键带宽） | `jitter-band.json` |
| 比对输出（三组） | `compare-run1-run1.txt`、`compare-run1-run2.txt`、`compare-run1-run2-measured.txt` |

## 2. 归一化与比对

- 共用 flatten：`benchmarks/WinForward.E2E/scripts/jsonl_paths.py`（盘点与比对同一份字母表；
  `join('/', 段)`，键内点号不拆，数组只贡献自己的路径、元素不展开成 `[i]`）。
- 配置：`../record-normalize.json`。
- 比对：`benchmarks/WinForward.E2E/scripts/compare-records.py`，范围 = 11 个 `<arm>.jsonl` + `run.json`
  + `ledger.jsonl` 的**路径集合与值**，外加 `target.out` 的文本归一化（时间戳、绝对路径）。
- 三栏：① 结构性（键集/类型/数组长度/字符串值）② 条件键（`UseTcp`/`UseUdp`/空直方图，单列，不判失败）
  ③ 数值（**逐键**抖动带，无全局 tolerance；带由 `--write-band` 从 run1↔run2 实测冻结）。

归一化只移出**构造上就不具可比性**的部分：时钟类串值（`*Utc` 等；它们的数值孪生 `ticks`/`*Ticks`/`*Us`
不归一化，进抖动带）、绝对路径键（`planPath`/`outDirectory`）、以及易变串值中的易变成分——账本 `peer`
只归一化**端口**（地址仍参与比对，见 `record-normalize.json` 的 `endpointKeyNames`），`notes` 只归一化
内插的**小数**测量值（F3 速率、F1 天花板），注记里引用的整数常量（256/16384/4096/1048576 等）逐字比对。
数值键一律不归一化，而是进逐键抖动带。

## 3. 自检结果

```
$ python3 compare-records.py run1 run1 --normalize record-normalize.json --band jitter-band.json
summary: structural=0 conditional=0 numeric=0 measured=903        # 退出码 0，三栏全空

$ python3 compare-records.py run1 run2 --normalize record-normalize.json --band jitter-band.json
summary: structural=0 conditional=0 numeric=0 measured=903        # 退出码 0，三栏全空
```

- **run1 vs run1 = 空 diff**（903 条数值路径的 maxAbsDelta 全为 0）。
- **run1 vs run2 = 只报抖动带内的差异**：在 band 强制下三栏皆空；未加 `--band` 时结构性差异也是 0，
  数值差异全部落在 `jitter-band.json` 记录的逐键带宽内（见 `compare-run1-run2-measured.txt`）。
- 两处曾经是"结构性差异"、经归一化后归零：账本 `peer` 的临时源端口（只端口归一化，地址仍比对）、
  BASE/LAT 的 `notes` 文案（其中内插的 achievedRate / windowCeilingMs 是小数，每次运行都不同）。
  两项的收紧版本（`record-normalize.json` 的 `version: 2`）在 check 报告里逐条验证：
  收紧后 `run1↔run2` 与 `run1↔run1` 仍全空，而"`peer` 地址变化""`arms[].file` 映射变化"
  "`notes` 里的整数常量变化"这三种今天会被归一化吃掉的变化重新变成结构性差异。

## 4. 抖动带宽度的量级

903 条数值路径中 **607 条两次运行完全相同**，296 条发生移动：

| 类别 | 条数 | 带宽量级（maxAbsDelta） |
|---|---|---|
| 时钟/采样类（`*Ticks`、`cpuSeconds`、`workingSetBytes`、`threads`、`pid`、`envWorkingSetBytes`…） | 244 | `ticks` ≈ 9.9e10（两次运行相隔约 16 分钟）；`cpuSeconds` 毫秒级；`Bytes` 级字段 1e5–1e6 |
| 统计/计数类 | 52 | 多数为 0–1 个计数单位；见下表 |

统计类里带宽最大的几条（`jitter-band.json` 内含全部 903 条）：

| 路径 | base | after | maxAbsDelta | 相对 |
|---|---|---|---|---|
| `records/THRU.jsonl::metrics/bytes` | 159965600 | 159932800 | 32800 | 2e-4 |
| `ledger/ledger.jsonl::sources/port` | [40572,56230] | [33410,56084] | 7723.68 | 0.177 |
| `records/THRU.jsonl::metrics/goodputBps` | 19992324.386 | 19989332.343 | 2992.04 | 1.5e-4 |
| `records/THRU.jsonl::metrics/frames` / `framesEchoed` | 4877 | 4876 | 1 | 2e-4 |
| `records/LAT.jsonl::metrics/udp.windowCeilingMs` | 203821.656 | 203841.943 | 20.287 | 1e-4 |
| `records/REL.jsonl::metrics/meanConnectMs` | 0.517 | 0.441 | 0.076 | 0.147 |
| `records/THRU.jsonl::metrics/bytesSent`、各 `metrics/*.sentOk`、`*.arrived`、`*.answered`、`gates.clientSendLoss` | — | — | **0** | 0 |

即：**计数类键在两次运行间逐字节相同**（`sentOk`/`arrived`/`answered`/`clientSendLoss` 等全为 0 带宽），
带宽非零的全部是速率、耗时、天花板与采样读数；`count` 统计另给 `allowedCountDelta = max(实测, 1)`，
使 ±1 个采样点的漂移不读成契约变化。

## 5. 复现

```bash
cd benchmarks/WinForward.E2E/scripts
B=../../../.trellis/tasks/10-07-e2e-harness-refactor/research
python3 compare-records.py $B/baseline/run1 $B/baseline/run1 --normalize $B/record-normalize.json --band $B/baseline/jitter-band.json
python3 compare-records.py $B/baseline/run1 $B/baseline/run2 --normalize $B/record-normalize.json --band $B/baseline/jitter-band.json
```

脚本只用 PATH 上的 `python3`（direnv 提供），缺失时显式报错退出，不依赖 `nix-shell`；selftest 的端口
31010/5301/5302 在运行前确认为空闲，未 kill 任何进程。
