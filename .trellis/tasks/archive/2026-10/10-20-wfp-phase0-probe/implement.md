# 执行计划 — Phase 0 探针

1. 在 VM 上把模板工程复制成 `wfprobe`，先编译通过空驱动，确认工具链可用。
2. 加 WFP 骨架：engine、redirect handle、callout 注册、filter，能加载不崩。
3. 加 classify：ANY 分支 → loopback 目标；构造 context；apply。
4. 写 C# 监听/客户端测试程序，验证重定向与上下文往返。
5. 加 IOCTL：下发 PID 与监听端口、回灌日志。
6. 测第二条地址分支（指定 local 地址）。
7. 验证上下文所有权（文档 + 200 次连接池用量）。
8. 写 `research/phase0-findings.md`，把结论、证据、失败尝试都记下来。
9. 源码留在 `src/driver/` 与 `tools/`；VM 上的临时产物不必回传。

每步都要能复现；失败的尝试同样记录下来。
