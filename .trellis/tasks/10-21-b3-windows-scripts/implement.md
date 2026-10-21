# B3 执行步骤

1. `git mv tools/vm tools/windows`；`git rm tools/windows/sync.sh`。
2. 写 `WfBuild.psm1`：所有外部工具发现都在这里，脚本里不再出现绝对路径。
3. `build-and-test.ps1` → `gate.ps1`：构建 → 签名 → 安装 → PREfast 分析 → 各套件 → 卸载。
4. `mutation-test.ps1` → `mutation.ps1`：**先只改名与薄壳化，匹配逻辑不动**（区域标记是 B4）。
5. `load-driver.ps1`、`firewall-check.ps1`、`userland-e2e.ps1`、`userland-socks5-e2e.ps1` 改用模块与新路径。
6. PREfast 判定：运行分析构建，解析输出——出现 `warning C28` 或 Summary 的 `Warning(s)` 非零即 FAIL。
7. README 同步：目录名、参数、`local/` 的职责。

## 同步到 Windows 的临时办法

仓库不再提供同步脚本。开发期间用一个 `/tmp` 下的一次性脚本完成 zip + `windows-vm <会话> up` + `Expand-Archive`，不要把它提交进仓库。

## 验证

- A1 的 `git grep`。
- A2：三个脚本分别在一台**没有** `C:\wfbench` 的路径下跑一次 `-Root` 显式指定的冒烟（至少 `-?`/参数解析与工具发现）。
- A3：完整 `gate.ps1` → `GATE-RESULT=PASS`。
- A4：摘 `_Dispatch_type_` → `gate.ps1` 非零退出；恢复。
- A5：`mutation.ps1 -OnlyIndex 0,3` → `MUTATION PASS`；`git status --porcelain` 干净。
