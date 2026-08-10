# CLI integration 执行计划

1. 从 `Main` 构建命令/依赖/CTS/owned-resource/exit-code 图，读取全部子任务的最终 report/contract。
2. 审读 validate/adapters/run 和异常/cleanup/log 路径；追踪 configuration-before-driver、fail-closed、redaction、adapter selection 与 disposal order。
3. 以可注入 seam 或子进程方式写稳定回归，确认 defect 后最小修复；不要在此重修拥有者的业务逻辑。
4. 运行所有 CLI-focused tests、全套 Release build/test/diff check；按需 Windows `build/test/publish`, `validate`, `adapters`, `run` smoke。
5. 写 findings/gaps report，供父任务生成最终审查摘要与 Windows validation matrix。
