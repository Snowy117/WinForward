# CLI integration 审查设计

以 composition root 为图：args/config -> command choice -> driver/adapter creation -> lifecycle/capture/dispatcher/coordinator wiring -> cancellation -> reverse-order cleanup -> exit/log。每一个 early failure 应验证没有不应打开的 driver/mode、无 secret 输出，并且收尾不会吞掉主错误。

Linux 测试只证明可注入的 composition/argument/error contract；Windows 真机记录 driver, adapter, packet direction, real SOCKS5, Ctrl+C 和 `win-x64` publish。任何 Linux 绿测不能替代这些硬件结果。
