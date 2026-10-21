# B5 · 禁止模式检查与提交前钩子

> 父任务：`.trellis/tasks/10-21-quality-gates-modernization`（design §4.5）

## Goal

把 `cpp-conventions.md` §5.1 的禁止清单变成会失败的自动检查，并让提交前钩子拦住格式或禁用写法；规范文档同步到"照着文档做就能得到这个仓库"。

## Scope

- 新增 `tools/lint/forbidden.py`、`tools/lint/forbidden-rules.txt`。
- `tools/check.sh`：`--fast` / `--full` 增加禁止模式步骤；`--staged` 下格式与禁止模式都检查**索引内容**。
- 新增 `.githooks/pre-commit`、`.githooks/install.sh`。
- `README.md`、`tools/windows/README.md`（若涉及）、`.trellis/spec/backend/cpp-conventions.md` §8、`.trellis/spec/backend/quality-guidelines.md`。

## Acceptance

- **A1** 当前代码上 `forbidden.py` 零命中，退出码 0。
- **A2** 在 `src/driver` 写入 `std::vector<int> x;` → 非零退出并报 `文件:行` + 规则 ID；加 `// WF-LINT-ALLOW(stl): 理由` 后通过。
- **A3** 空理由的豁免（`WF-LINT-ALLOW(stl):` 后面没内容）必须被判为无效，不能被当成豁免。
- **A4** `tools/check.sh --fast` 包含该检查且总耗时 < 5 s；`--staged` 检查的是暂存区内容（暂存脏、工作区干净 → 失败）。
- **A5** 安装钩子后，暂存一处格式漂移并提交 → 被拦下；`git commit --no-verify` 仍可绕过；卸载钩子（`core.hooksPath` 复原）是一步可逆操作。
- **A6** 规范文档与实现一致：§5.1 的八条在 §8.4 里有明确归属（4 条自动——表里按规则拆成 5 行；4 条评审）；规则文件与 §8.3 的 ID/覆盖范围一致（`stl` 是 blocklist 摘要并注明完整清单位置），豁免语法在文档里写明；已知的正则局限（跨 token 空白等）在 §8.3 注明。

## Out of Scope

- CodeQL（B6）。
- C# 侧检查。
- 规则库扩张到 §5.1 之外的风格问题。
