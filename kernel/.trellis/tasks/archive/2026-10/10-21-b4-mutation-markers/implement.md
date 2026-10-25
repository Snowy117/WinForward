# B4 执行步骤

1. 读 `tools/windows/mutation.ps1`，列出 16 条用例及其 `Old` 文本。
2. 在源码里为每条 `Old` 定位精确范围，插入：

```cpp
    // <mutation id="kebab-case-id">
    ...被替换的语句...
    // </mutation>
```

   标记放在被替换语句的首尾，缩进与语句一致；id 用短横线小写，语义清晰（如 `gate-listener-port`）。
3. 改写 `$mutations`：`Id = "..."`、`New = "..."`（替换文本保持原来的语义），保留 `Name`、`Target`。
4. 改写应用逻辑：
   - 用正则匹配 `// <mutation id="Id">` 到 `// </mutation>` 之间的内容；
   - 命中数必须为 1，否则立即失败并打印 id；
   - 用 `New` 替换区域内容，保留标记行；
   - 全部跑完后与原始内容逐字节比较。
5. 在 Windows 上全量跑一次 `mutation.ps1`（16 条 + 基线）。

## 验证

- A3 全量输出；A4 的错 id 反向验证；A5 的格式与门禁；A6 的 `git status`。
