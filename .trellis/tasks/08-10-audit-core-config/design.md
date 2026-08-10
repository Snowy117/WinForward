# Core / Configuration 审查设计

以“JSON -> validated configuration -> policy -> canonical flow key/ownership”追踪每项数据。每一个接受或拒绝路径必须能解释其保留的规范化值、错误路径和是否可能暴露 secret。`Domain`/`PacketRuntime` 的键约束须与 Runtime 的实际索引和过期路径交叉比对，不能只读本项目。

测试预言机以公开的配置/策略输出为主：输入边界、选择的 rule/action、错误位置/是否 redacted、以及端点/flow 等价性；不直接把内部实现细节当预言机。已修复的 upstream 调用问题仅交叉引用，根因仍由拥有本源码的本项报告。

主要风险是将“未知 owner 不匹配”误变成阻断或错误匹配，以及错误 JSON exception 文本泄露数据。发现需要改变用户可见 JSON/CLI contract 时停止修复并提交父任务决策。
