# Runtime capture / flow 审查设计

用“每个捕获帧有一个 terminal disposition”作为主不变量。为每个 early return、exception、cancel 和 dispose 路径画出 lease、CTS、pump task、adapter mode 和 driver handle 的获取/释放顺序；重新注入必须与 packet origin/方向配套。

审查先于 coordinator：dispatcher 只决定 flow/action 的边界，TCP/UDP 状态机缺陷转交相应子任务。并发/关闭测试以 `TaskCompletionSource` 或可观察 barrier 固定时序，不能用调度幸运性作为证明。
