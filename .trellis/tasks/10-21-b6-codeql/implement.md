# B6 执行步骤

1. `tools/windows/codeql.ps1` 参数：`-Root`（默认仓库根）、`-CodeqlHome`、`-Database`（默认 `<Root>/build/codeql/db`）、`-Output`（默认 `<Root>/build/codeql/wf.sarif`）、`-SkipDatabase`（复用已有库只重跑分析）。
2. 工具发现：`-CodeqlHome` → `$env:CODEQL_HOME` → PATH；找不到 → 环境错误 exit 2。
3. 依赖：检查 `microsoft/windows-drivers` 与 `microsoft/cpp-queries@0.0.5` 是否已安装，缺失时 `codeql pack download`。
4. 建库：`codeql database create <db> --language=cpp --command "<msbuild ... -restore -t:Rebuild>"`。
5. 分析：`codeql database analyze <db> "microsoft/windows-drivers:windows-driver-suites/recommended.qls" --format=sarif-latest --output <sarif>`。
6. 输出摘要：结果条数、按规则分组、耗时；SARIF 解析失败要报错而不是当成 0 条。

## 验证

- A1：把 `codeql` 从 PATH 拿掉并清空 `CODEQL_HOME` → exit 2。
- A2/A3：干净树跑一次，SARIF 非空、记录耗时。
- A4：注入 `if (x + 1 < x)` → 结果里出现对应查询；恢复后与基线一致。
- A5：README 写清前置条件与用法。
