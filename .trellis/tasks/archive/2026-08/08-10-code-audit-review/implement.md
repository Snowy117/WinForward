# 全项目代码审查执行计划

## 前置门禁

- [x] 已将八个子任务建为父任务 `08-10-code-audit-review` 的子项，并给每项写明独占文件、依赖和验收标准。
- [x] 已在源码修改前记录生产文件清单，并于 2026-08-10 重新验证 `dotnet build WinForward.slnx -c Release`（0 warning / 0 error）和 `dotnet test WinForward.slnx -c Release`（149 passed / 0 failed）。
- [x] 已为父/全部子任务写入经过校验的真实 `implement.jsonl` / `check.jsonl` 上下文条目。
- [ ] 每次修改产品代码前加载 `trellis-before-dev` 的适用 backend 规范；跨越三层以上的数据流额外读取 cross-layer thinking guide。

## 统一审查方法（每个子任务都执行）

1. 用源文件清单逐文件建立“已读/调用者/被调用者/测试/结论”工作表；不能因没有发现问题而省略文件。
2. 对数据边界进行手工追踪：配置/NDIS frame/socket bytes -> 解析/分类 -> policy/flow ownership -> action/reinject -> 生命周期释放。对协议字节与 checksum 使用 RFC 和独立测试预言机，不只信任已有生产 helper。
3. 仅将可以给出文件、行号、输入/并发时序、预期与实际行为的现象写为 finding；复核既有报告/近期提交，避免把已修复问题重新报出。
4. 每个可在单测复现的 confirmed finding：先写稳定的回归测试，确保它在修复前会失败（可通过临时局部验证或逻辑证明确认，不能提交红套件），实现最小修复后运行 focused test 和全套测试。
5. 将 findings、无问题文件、测试名称、未覆盖分支、Linux 限制和 Windows gate 写入子任务 `research/`。固定格式为 `ID | severity | type | file:line | evidence | reproduction | fix | regression test | status`。
6. 运行子任务 focused test、`dotnet build WinForward.slnx -c Release`、`dotnet test WinForward.slnx -c Release`、`git diff --check`；报告精确通过数与 warning/error 数。

## 子任务路线图

### A. 独立基础层（可并行只读审计；生产修改按文件所有权）

- [ ] **`audit-core-config`**：审读 Core 与 Configuration，锁定规则顺序、selector、flow key、CIDR、JSON source generation、validation 和红action。测试/报告应明确 omitted vs empty、IPv4/IPv6、大小写、UTF-8 长度、未知字段与凭据脱敏。
- [ ] **`audit-protocols`**：逐条核对 RFC 1928/1929、RFC 768 及实现声明的 IP/TCP 规则。测试 parser 截断/长度/fragment、IPv6 extension chain、ATYP/REP/FRAG、checksum 独立复算、round trip、拒绝不改写。
- [ ] **`audit-ndisapi`**：检查 `LibraryImport`、struct pack/size、safe handle、last-error、buffer/adapter-handle 生命周期。Linux 以 ABI/fake 覆盖；真实 driver 结果登记到 Windows gate。
- [ ] **`audit-windows`**：检查 adapter GUID/MAC fallback、IP Helper 缓冲遍历、PID/path/creation time、IPv4/IPv6 scope 与归因失败。为能脱离 Windows 的 projection/ABI 分支补纯测试。

### B. 数据面状态机（在 A 的契约/发现完成后）

- [ ] **`audit-runtime-capture-flow`**：以每个捕获包的 terminal disposition 为主线，审读 scope resolver、pump、dispatcher、executor、reinjector、mode controller、expiry、self-traffic。优先复核未等待 pump handler 即 restore/dispose 的既有高风险线索；测试取消、失败、重入、adapter 方向与 exactly-once 释放。
- [ ] **`audit-runtime-tcp`**：审读 TCP redirect 的 SYN/retransmit/reverse/data/FIN/RST/accept/relay/expiry 路径。重点确认 tuple index 的 protocol/AF/origin/generation 隔离、并发 claim、所有 socket/token 释放及 `ON_SEND`/`ON_RECEIVE` 方向。协议 rewrite 的字节预言机归 `audit-protocols`，本项引用它而不重复所有权。
- [ ] **`audit-runtime-udp-socks`**：审读 DNS/任意 UDP flow 的 SOCKS control、UDP ASSOCIATE、relay alias、wildcard self-traffic、response reinject、并发 setup/cancellation/expiry。覆盖 dynamic relay port、IPv4-mapped/unspecified address、scope id、association collision 与 forwarded origin。

### C. 集成与汇总（在 B 后）

- [ ] **`audit-cli-integration`**：追踪 CLI 的 `validate`/`adapters`/`run`、exit code、启动和 dispose 顺序、configuration-before-driver、redaction 和 publish 输入。新增可行的 process-level/抽象 seam 测试；记录 Linux 无法证明的行为。
- [ ] **父任务整合**：合并子报告，产出 module coverage matrix、去重 summary、coverage gaps 和 Windows validation 文档；检查每个 confirmed finding 的报告/测试一一对应，每个 41 个生产文件被恰好一个子任务审读。

## Windows 验证门禁

在只要涉及 NDISABI、capture/reinject、adapter identity、TCP/UDP proxy 或 CLI run 的修改后，判定是否需要 Windows 验证。若需要，使用用户提供的 Windows 开发机，但先保持 WinRM 子网为 `pass`，避免以 catch-all rule 截断管理连接。记录而非假设以下项目：

- x64 driver/DLL ABI 与 `adapters` 输出；
- tunnel pass/block 的单份与正确方向回注；
- host IPv4/IPv6 TCP CONNECT、UDP ASSOCIATE 与动态 relay port；
- 有真实 VM 时的 forwarded/Hyper-V TCP/UDP；
- loop prevention、代理不可用时 fail-closed、Ctrl+C 后 adapter restoration；
- `dotnet publish ... -r win-x64` 的 Native AOT 启动。

若开发机、远程 SOCKS5 或真实 Hyper-V guest 不可用，文档必须将相应行为标为 **not executed / pending hardware gate**，不能以 Linux 绿测替代。

## 最终质量与归档门禁

- [ ] 每个子任务报告的 finding 有 severity、type、精确 `file:line`、证据、复现路径、修复状态和测试映射。
- [ ] 每个可单测复现的 confirmed bug 有绿色回归；无法单测的 finding 包含明确的 Windows/集成复现步骤。
- [ ] 父 `research/module-coverage-matrix.md` 覆盖所有 41 个生产源文件，且列出每个无 bug 结论的依据。
- [ ] 父 `research/coverage-gaps.md` 以“模块 × 行为/分支 × 为什么不能现有测试证明 × 推荐 gate”列出缺口。
- [ ] `dotnet build WinForward.slnx -c Release` 零 warning/zero error，`dotnet test WinForward.slnx -c Release` 全绿，`git diff --check` 全绿；报告记录基线与结束状态。
- [ ] 对所有跨子任务 finding 做一次独立审查，确认没有重复修复、没有将静态猜测升级为 confirmed bug、没有遗漏 regression test。

## 风险与回滚点

| 位置 | 风险 | 控制/回滚 |
| --- | --- | --- |
| parser/checksum/packet rewrite | 单字节变更可破坏透明转发 | 先用独立 oracle、mutable-offset 和 round-trip 测试；失败立即回滚该最小修复 |
| flow/association tables | 并发测试假绿或 alias 串流 | TCS/barrier 证明重叠，断言可观测 loser/collision 结果；不依赖 `Task.Yield()` |
| capture 停机/NDIS handle | restore/dispose 与 in-flight handler 竞态 | 以 cancellation + await 为设计门槛；只在安全的 Linux seam/Windows host 验证后合入 |
| Windows 硬件 | 远程机失联、driver 环境差异 | 测试前保留 WinRM subnet pass；记录 driver/DLL/config；Ctrl+C 验证 rollback |
| 共享测试项目 | 并行写入同一大测试文件导致冲突 | 新增模块专属测试文件优先；修改既有文件时串行化并在报告说明 |
