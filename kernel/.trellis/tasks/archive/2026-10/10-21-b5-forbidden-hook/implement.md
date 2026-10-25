# B5 执行步骤

1. 写 `tools/lint/forbidden-rules.txt`：每行 `规则ID <TAB> glob <TAB> 正则 <TAB> 说明`，`#` 注释。规则见 design §4.5（stl / exceptions / rtti / heap），范围 `src/driver/**`、`src/common/**`。
2. 写 `tools/lint/forbidden.py`：
   - 输入为文件列表（默认 `git ls-files`），剥离注释与字符串后再匹配；
   - 豁免只看**原始行**里的 `// WF-LINT-ALLOW(规则ID): 理由`（本行或上一行），理由为空视为无效豁免并报错；
   - 输出 `文件:行: [规则ID] 说明`，命中即退出码 1。
3. 接入 `tools/check.sh`：放进 `run_fast_checks`；`--staged` 复用已有的索引镜像树，检查暂存内容。
4. 新建 `.githooks/pre-commit`（调 `tools/check.sh --fast --staged`）与 `.githooks/install.sh`（设置/复原 `core.hooksPath`）。
5. 文档：`cpp-conventions.md` §8 写清门禁清单与豁免语法；`quality-guidelines.md` 的空模板替换为指针；`README.md` 写钩子安装与卸载。

## 验证

- A1/A2/A3 的直接注入实验（含空理由豁免）。
- A4：`--fast` 计时；临时仓库验证 `--staged` 的索引语义（暂存脏/工作区干净）。
- A5：真实 `git commit` 被拦、`--no-verify` 绕过、卸载后恢复正常。
- A6：逐条比对 §5.1 与规则文件。
