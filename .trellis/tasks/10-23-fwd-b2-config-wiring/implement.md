# 执行计划：B2 Forwarded 配置、多上游与 clientCidr 接线

> 只改用户态配置/控制器与测试；不碰 `src/common/*`、驱动、B1 的 ABI 与 blob 布局。

## 0. 读上下文

- B1 已归档的规则层：`src/userland/WinForward.Userland/Forwarded/`、
  `src/common/forwarded_blob.h`、`src/common/forwarded_matcher.h`。
- 现有配置/控制器：
  - `src/userland/WinForward.Userland/Configuration/PolicyConfigurationDocument.cs`
  - `Configuration/ControllerConfiguration.cs`、`Configuration/Socks5Endpoint.cs`
  - `Control/ControllerSettings.cs`、`Control/WfpController.cs`
  - `src/userland/WinForward.Controller/RunCommand.cs`、`CheckCommand.cs`
- 测试：`src/userland/WinForward.Userland.Tests/ConfigurationTests.cs`、`Program.cs`
- 本任务 `prd.md`、`design.md`

## 1. 配置模型与解析

- [ ] 新增 `Forwarded/IAdapterIdResolver.cs`：按 `design.md` §2 定义。
- [ ] `ControllerConfiguration` 增加 `Forwarded`、`Upstreams`。
- [ ] `PolicyConfigurationDocument`：
  - [ ] `Load`/`LoadConfiguration`/`Parse`/`ParseConfiguration` 增加可选
        `IAdapterIdResolver? adapterResolver = null`，保持旧调用点不变。
  - [ ] 根字段白名单加 `socks5Servers`、`forwarded`。
  - [ ] 解析 `socks5Servers`（name/host/port、唯一性、与 `default` 冲突）。
  - [ ] 解析 `forwarded`（fallback、rules、adapter、clientCidr、remoteCidr、remotePorts、
        protocol、family、action、target、process 拒绝）。
  - [ ] 诊断路径 `forwarded.rules[i].<field>`；错误消息带规则名（若有）。
- [ ] 旧配置（无 `socks5Servers`/`forwarded`）行为不变。

## 2. 控制器接线

- [ ] `ControllerSettings` 增加 `Forwarded`、`ForwardedBlob`、`Upstreams`。
- [ ] `RunCommand` / `CheckCommand` 构造 settings 时编译 forwarded blob
      （`ForwardedBlobCompiler.Compile`）；`CheckCommand` 报告规则数/上游数。
- [ ] `WfpController.Start` 只报告 `controller-forwarded ...`；**不新增设备调用**，
      不改变现有启动/清理顺序。
- [ ] 编译失败走现有配置错误路径（退出码 2 或等价），不要让控制器半启动。

## 3. 测试

- [ ] 新增 `ForwardedConfigurationTests.cs`，覆盖 `design.md` §6 的 1–5。
- [ ] `FakeAdapterIdResolver`（测试内）映射 name/id → 固定 uint。
- [ ] 在 `Program.cs` 的 `RegisterAll()` 注册新测试类。
- [ ] 现有 `ConfigurationTests` 全绿；必要时只加断言，不改既有期望。

## 4. 文档

- [ ] `PolicyConfigurationDocument` 的 XML 注释更新 schema。
- [ ] `.trellis/spec/backend/forwarded-rule-blob.md` 增加“配置映射”一节。
- [ ] 父任务 `design.md` D3 补一句配置落点（可选，若时间允许）。
- [ ] `src/userland/README.md` 补 forwarded 配置段（若该文件已有配置说明）。

## 5. 验证

- [ ] `dotnet build src/userland/WinForward.Userland.slnx`（0 warning / 0 error）
- [ ] `dotnet run --project src/userland/WinForward.Userland.Tests -- test`
- [ ] `cmake --build build --target wf_check`（确认 C++ 侧不受影响）
- [ ] `tools/check.sh --fast`
- [ ] 手工：`dotnet run --project src/userland/WinForward.Controller -- check --config <临时配置>`
      能报告 forwarded 规则数/上游数；非法配置给出路径。

## 6. 报告格式

1. 改了哪些文件，各自一句话；
2. 关键决定（JSON 字段、adapter 解析、target 解析、控制器报告）；
3. 验证命令与结果摘要；
4. 计划没覆盖、需要主会话决策的点。

## 回滚点

- 只改用户态配置/控制器；失败可整批回退，B1 规则层与 host 控制器行为不受影响。
