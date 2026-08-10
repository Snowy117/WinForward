# TCP redirect 审查设计

按一条连接的状态追踪：原始 SYN -> translated listener tuple claim -> listener accept -> SOCKS CONNECT relay -> client/listener data -> reverse packet -> FIN/RST/error/expiry。每一步记录原始流键、translated alias、origin adapter、方向、持有的 listener/socket/self-traffic token 和释放责任。

同 key 并发 SYN 的唯一裁决是 association table claim；其余竞争者必须关闭刚分配的 listener、复用已存在的关联并重新注入。任何 setup/rewrite/inject/relay 故障均消费 proxy flow、释放已获得资源，不能偷偷 pass。
