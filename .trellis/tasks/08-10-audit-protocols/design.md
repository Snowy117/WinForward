# Protocols 审查设计

审查分两层：先验证每个 parser/frame builder 的长度、offset、ATYP/next-header 和错误返回；再验证 mutation primitive 的伪首部、长度、0 值和原帧不变量。测试对 checksum 独立实现 `Sum`/`Finish`，并显式列出允许改写的字节 offset。

SOCKS5 测试把流式读取的 prefix 与完整 reply 分开，避免把完整 parser 误用于未读全的响应。任何“符合 RFC”结论必须同时检查 Runtime 的调用长度与返回值处理；跨界错误在报告中交叉链接其拥有者。
