# 设计 — SOCKS5 上游

## 分层

| 位置 | 职责 |
|---|---|
| `Configuration/` | 解析可选的 `socks5` 段，产出 `Socks5Endpoint` |
| `Relay/Socks5Client.cs` | 与上游建连并完成 CONNECT 握手，返回已连通的 socket |
| `Relay/RelayServer.cs` | 出站连接抽象成「拿一个到 original destination 的 socket」；有上游走 SOCKS5，没有就直连 |

## 握手

标准 RFC 1928：`05 01 00` → `05 00`；然后 CONNECT：

- IPv4：`05 01 00 01 <4 字节> <2 字节端口>`
- IPv6：`05 01 00 04 <16 字节> <2 字节端口>`
- 域名：`05 01 00 03 <长度> <ASCII> <2 字节端口>`

回复 `05 <rep> 00 <atyp> <bnd.addr> <bnd.port>`，`rep != 0` 即为失败。

## 错误

- 建连超时沿用现有 10 秒预算。
- 握手失败要带上 `rep` 码与目标，抛出具名异常，由中继的会话级 catch 打印 `relay error #…`。
- **不允许**静默退化回直连——那会让"代理没生效"看起来像成功。

## 测试替身

`tools/socks5/`：并发 accept、每连接只做握手与 CONNECT、然后双向转发到真实目标。记录 `connections=`、`connects=`、`bytes=`，供 E2E 断言上游真的被用到。
