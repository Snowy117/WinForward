# 设计 — C# 侧 blob 编译器

## 工程位置

`src/userland/` 下一个 .NET 工程（`WinForward.Userland`），本批只含 `Policy/` 命名空间下的编译逻辑与测试。

工具链：仓库 devShell 需要 `dotnet`。若当前 flake 没有，就加 `dotnet-sdk_10`（目标框架 `net10.0`），并在 `.trellis/spec` 里记一句。

## 类型镜像

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct RuleBlobHeader
{
    public uint Magic;            // 0x31425257 "WRB1"
    public ushort Version;        // 1
    public ushort HeaderSize;     // 40
    public uint TotalSize;
    public uint RuleCount;
    public uint FallbackAction;
    public uint RulesOffset;
    public uint ProcessPoolOffset;
    public uint ProcessPoolSize;
    public uint NetworkPoolOffset;
    public uint NetworkPoolSize;
}
```

规则 16 字节，字段顺序见 `rule_blob.h`。

**断言方式**：`Marshal.SizeOf<RuleBlobHeader>() == 40`、`Marshal.OffsetOf<RuleBlobHeader>(nameof(...))` 逐字段比对；写成一个测试用例，值直接照抄 `rule_blob.h` 的数字。

## 布局

推荐顺序 `header | rules | 进程串池 | 网段池`，各段起点 4 字节对齐。

- `process_pool_size` 写精确串字节数（不含对齐填充）；
- `network_pool_offset` 向上对齐到 4；
- 进程串池去重后按首次出现顺序排布，索引指向串首；
- 网段池每条 20 字节，`network_index` 是其字节偏移。

## 差分测试怎么接

C++ 侧提供一个测试入口（`tests/host/` 下新增一个可执行或 gtest 用例）读取 blob 文件并输出决策；C# 测试把它编译成"生成 blob → 交 C++ 消费 → 比对"。

两种可行形态，任选其一并写进 implement：

1. C# 测试把 blob 与"期望决策表"写成 JSON，C++ 侧 gtest 读取并逐条断言（纯离线，最省事）；
2. C++ 侧编译一个小可执行，C# 测试调用它（跨进程，边界更真实）。

**倾向方案 1**：不引入跨语言调用复杂度，失败信息也更好读。

## 风险

| 风险 | 应对 |
|---|---|
| 两侧对"哪个字段是 host order"理解不同 | 全部主机字节序，断言里写死字节序列做对照 |
| 进程串大小写归一不一致 | 编译期统一小写，差分测试用大写规则串验证 |
| 空表 blob 的段偏移落在 `total_size` 端点 | 已有用例覆盖，C# 侧照抄 |
