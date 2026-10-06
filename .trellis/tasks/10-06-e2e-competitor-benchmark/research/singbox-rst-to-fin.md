# sing-box SOCKS5 inbound 把上游 RST 变成 FIN

**版本**：1.14.2（`git describe` = `v1.14.2`，本地构建标记 `1.14.2-singfix`，后者只含另一处
与本报告无关的握手补丁）
**入口**：socks inbound，TCP CONNECT
**影响**：下游客户端无法区分「上游对端重置了连接」和「上游对端正常关闭」

## 现象

用 sing-box 作为 SOCKS5 上游时，一个「读完 N 字节后发 RST」的对端，在客户端看来变成了一次
**干净的 EOF**。把 sing-box 从链路里拿掉、其余一切不变，同一个对端的同一次 RST 就能正常到达
客户端。

## 源码

`sing/common/bufio/copy.go`，`CopyConn`：

```go
group.Append("upload", func(ctx context.Context) error {
    err := common.Error(Copy(destination, source))
    if err != nil {
        common.Close(destination)
        return err
    }
    ...
```

上游读出错（ECONNRESET 就是一个非 EOF 的读错误）时走的是 `common.Close`。
`common.Close`（`sing/common/cond.go`）按 `io.Closer` 调 `c.Close()`——TCP socket 上还有未读
数据时，`close()` 发出的是 **FIN**，不是 RST。

整棵源码树里 `SetLinger` 只出现一处：

```
common/network/handshake.go:48    SetLinger(sec int) error
common/network/handshake.go:50    tcpConn.SetLinger(0)
```

在握手失败路径。中继路径上从不使用 `SO_LINGER(0)`。所以上游的 RST 到不了客户端那一侧。

## 复现

不需要真实应用。用一个会讲 SOCKS5 的服务端替掉真正的目标，让它**回显满客户端期望的字节数**
之后再按指定方式关闭：

- 模式 A：`setsockopt(SOL_SOCKET, SO_LINGER, {1, 0})` 后 `close()` → 发 RST
- 模式 B：直接 `close()` → 发 FIN

客户端每次连接发固定字节数然后等回显，共 301 次，统计它观测到的关闭语义。

**回显量必须对齐**：如果服务端少回一些就关闭，那是一个「提前中断」，中继会把它当成自己的故障
并注入自己的 RST——实验会因此得出完全相反的结论。这一点我们踩过，所以写在这里。

### 结果

| 末端关闭方式 | 无 sing-box | 经 sing-box |
| --- | --- | --- |
| RST（`SO_LINGER(1,0)`） | reset **281** / eof 0 | reset **1** / eof **300** |
| FIN（正常关闭） | reset **301** / eof 0 | reset **0** / eof **301** |

同一个假服务端、同一种关闭方式、同一个客户端、同一个下游代理，唯一变量是链路中有没有
sing-box。有它时 301 次里最多 1 次 reset。

## 为什么这件事值得修

SOCKS5 CONNECT 不携带「对端是 RST 还是 FIN」这个信息，所以一旦 sing-box 在中间把它抹掉，
下游**没有任何办法**恢复。这不是一个下游可以补偿的损失。

对应用的实际影响：

- **HTTP/1.1**：RFC 9112 对「响应中途连接关闭」的处理，取决于关闭是 clean 还是 error。干净
  关闭意味着响应不完整且不可重试，而连接错误在幂等请求上是可重试的。两者被合并后，客户端会
  做出不同的重试决策。
- **长连接池**：一个被 RST 的连接和一次正常的服务端关闭，在连接池的健康判断上是两回事。
- **诊断**：下游只能报告「对端关闭」，而真实事件是「对端重置」。我们这次的排查就因此走了弯路
  ——一份把链路中间那一跳漏掉的报告，把上游的 RST 丢失归因成了下游代理的缺陷。

## 修复方向

在 `CopyConn` 的错误分支上，对上游读错误用一个带 `SO_LINGER(0)` 的关闭把 RST 传下去，而不是
`common.Close`。这个模式在同一棵树的 `handshake.go` 里已经存在，直接复用即可。

需要注意的边界：只有**上游读**的错误才应该这样做；目的端（客户端那一侧）自己断开时不应反向
注入 RST，否则会把正常关闭变成假的重置。`CopyConn` 的两个方向各自有独立的错误分支，因此可以
只改 upload 方向。

## 与本仓库已有的工作关系

这是**第二个**独立的上游问题。第一个是 SOCKS5 握手用 `bufio.Reader` 多读、吞掉客户端在 CONNECT
之后流水线发出的首批字节，报告与补丁在 `sing-socks-pipelined-payload.patch`。两个问题互不相干，
可以在同一个 issue 里一起提，也可以分开。
