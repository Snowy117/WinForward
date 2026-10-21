# B1 · 宿主门禁入口与 cppcheck

> 父任务：`.trellis/tasks/10-21-quality-gates-modernization`（prd/design/implement 是权威来源）

## Goal

建立宿主侧单一检查入口 `tools/check.sh`，把格式、有效行、cppcheck 三项接进去；用 cppcheck 的 `unusedFunction` 发现并删除 `block_classification`。

## Scope（只改这些）

- `flake.nix`：devShell 增加 `cppcheck`。
- `.gitignore`：增加 `wf.local.json`、`local/`。
- `wf.local.example.json`：新增，字段契约见父任务 design §2.2。
- `tools/check.sh`：新增，`--fast`（默认）/ `--full` / `--staged` / `--build-dir`。
- `src/userland/scripts/check-effective-lines.py`：默认目标改为 `src/` + `tests/`。
- `src/common/redirect_context.h`：给 `try_read_original_destination` 加 `// cppcheck-suppress unusedFunction` 与理由。
- `src/driver/probe_classify.cpp`：删除 `block_classification`。
- `tools/vm/mutation-test.ps1`：把依赖该函数的那条用例的替换文本改为内联等价代码（**不**做区域标记迁移，那是 B3）。
- 根 `README.md`：新建，写清质量门禁用法与本地配置约定。

## Acceptance

- **A1** `tools/check.sh --fast` 全 PASS，退出码 0，墙钟 < 5 s。
- **A2** 制造格式漂移 → 退出码 1 并指出文件；恢复后通过。
- **A3** 向 `src/driver` 注入未引用的匿名命名空间函数 → 退出码 1 并指出文件与符号；删除后通过。
- **A4** 新建超限 .cpp（>500 有效行）→ 有效行检查报出。
- **A5** `block_classification` 在代码中不再存在：`git grep -n block_classification -- src tools` 零命中（任务文档中的历史描述不算）。
- **A6** 缺少 `wf.local.json` 时不报错，也不产生与它相关的任何 FAIL。

## Out of Scope

- `--full` 的 clang-tidy 接入（B3 之后统一处理）。
- 变异用例的区域标记迁移（B3）。
- 任何 Windows 侧脚本改动。
- 禁止模式检查（B4）。
