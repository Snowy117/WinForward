# UDP / SOCKS runtime 审查设计

为一个逻辑 UDP flow 建立所有权链：flow key -> association claim -> control TCP socket (SYN 前登记) -> SOCKS UDP relay socket -> dynamic relay alias -> response frame/injection -> activity/expiry/dispose。原始 key 和 relay alias 必须互相可验证而不能只按 PID、DNS ID 或端口匹配。

共享 setup 只能由一个流拥有；等待者取消不应销毁共享会话。任何 connect/parse/send/reinject failure 都要释放已拥有资源并消费 proxy-selected flow。每项 frame/endpoint 解释依赖 protocols 的 byte correctness，Windows adapter/direction 则依赖 platform contract。
