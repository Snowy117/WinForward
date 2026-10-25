# Quality Guidelines

> 本仓库的质量标准只有一份事实来源：`.trellis/spec/backend/cpp-conventions.md`。
> 这份文件不重复它的内容，只说明每条标准在哪里执行、由谁检查。

## 标准 → 执行

| 关注点 | 事实来源 | 执行方式 |
|---|---|---|
| 格式 | `cpp-conventions.md` §8.1 | `.clang-format`；`tools/check.sh` 的格式步骤（`--fast` 看工作区，`--staged` 看索引） |
| 命名 | §3 | `.clang-tidy` 的 `readability-identifier-naming` |
| 文件有效行 | §4 | `src/userland/scripts/check-effective-lines.py`；`tools/check.sh` 的有效行步骤 |
| 内核方言的禁止写法 | §5.1 | `tools/lint/forbidden.py` + `tools/lint/forbidden-rules.txt`；逐条对应见 §8.4 |
| 错误处理与 RAII | §5.3、§7 | 评审 |
| 测试与变异 | §9 | 宿主 `ctest`（`--full` 的 `wf_check`）与 `tools/windows/mutation.ps1` |
| 门禁脚本自身 | §9.4 | 每条断言都要有「故意破坏后变红」的反向验证 |

## 门禁入口

```bash
tools/check.sh --fast     # 提交前必过：格式、有效行、cppcheck、禁止模式
tools/check.sh --full     # 合并前必过：--fast + 配置构建 + clang-tidy + wf_check
tools/check.sh --staged   # 钩子用：格式与禁止模式只看索引内容
```

退出码：`0` 通过、`1` 有发现、`2` 环境或参数问题。环境问题不算成代码问题。

提交前钩子：`.githooks/install.sh` 安装、`.githooks/install.sh --uninstall` 卸载，细节见
`cpp-conventions.md` §8.5。

## 评审检查单

- 新代码有没有命中 `forbidden-rules.txt` 的规则？命中时写的豁免有理由吗？放宽规则、空理由豁免都不接受。
- 新增的门禁断言做过反向验证吗？只打印不断言的脚本不算门禁（§9.4）。
- 用例的通过原因是不是就是它的名字？变异之后它会不会变红（§9.1、§9.2）？
- 内核侧的失败路径是否复用 `NTSTATUS`、用户态是否复用 Win32 错误码（§7）？
- 跨语言差分测试之外，规范化不变量有没有单侧用例覆盖（§9.5）？
