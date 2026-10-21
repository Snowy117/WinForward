# B1 执行步骤

1. `flake.nix` devShell 增加 `cppcheck`。
2. `.gitignore` 增加 `wf.local.json`、`local/`；新建 `wf.local.example.json`（含 `vm.session`/`vm.root`/`remote`/`codeql_home` 四个键的示例）。
3. 写 `tools/check.sh`：
   - 环境自检：缺 `clang-format`/`cppcheck`/`python3` → `exit 2` 并提示 devShell；
   - `--fast`：格式 → 有效行 → cppcheck；
   - `--full`：fast + 配置 `build/check` + 构建 + `wf_check`（tidy 留空，B3 再接）；
   - `--staged`：格式只查暂存区文件；
   - 每步 `[check] <名称> ... PASS|FAIL|SKIP`，末尾汇总与总耗时；退出码 0/1/2。
4. `check-effective-lines.py` 的 `DEFAULT_TARGETS` 改为 `src/`、`tests/`。
5. `redirect_context.h` 的 `try_read_original_destination` 加内联抑制与理由。
6. 删除 `probe_classify.cpp` 的 `block_classification`。
7. `mutation-test.ps1` 中把调用 `block_classification` 的替换文本改为内联等价代码（保持原有精确匹配机制）。
8. 根 `README.md`：仓库一句话 + 质量门禁用法 + `wf.local.json` 说明。

## 验证

- `nix develop -c tools/check.sh --fast` → PASS，退出码 0（当前 shell 可能没有 cppcheck）。
- 制造格式漂移 → 退出码 1；恢复。
- 注入 `namespace { void wf_probe_dead() {} }` 到 `probe_classify.cpp` → 退出码 1 且报出符号；删除。
- 新建 501 有效行的临时 .cpp → 有效行报出；删除。
- `git grep -n block_classification` → 只剩 `mutation-test.ps1` 里没有该符号（应为零命中）。
- `tools/check.sh --full` → PASS。
