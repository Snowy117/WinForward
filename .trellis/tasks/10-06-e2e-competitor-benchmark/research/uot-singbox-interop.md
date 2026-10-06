# UoT v2 does not interoperate with sing-box: the pipelined request is lost

Status: **confirmed on the VM, cause located in sing-box's source.**
Date: 2026-10-06. Product under test: CI artifact of `a247658`. Proxy server: sing-box 1.14.2.

## Symptom

With `"udpOverTcp": true` on the SOCKS5 target, every WinForward UDP flow fails. The client sees
100 % UDP loss, the target's ledger records zero datagrams, and sing-box logs one failure per flow:

```
INFO  inbound/socks[in]: inbound connection to sp.v2.udp-over-tcp.arpa:0
ERROR inbound/socks[in]: process connection from 127.0.0.1:58042: UoT read request: unknown address family: 152
```

The UDP workload is completely dead: a 4001-datagram loss arm returned `arrived=0`. The DNS arm is
unaffected because it is served by a `localTargets` entry and never reaches sing-box.

## What WinForward actually writes is correct

A SOCKS5 spy was run on the VM at `127.0.0.1:1081` (the WinForward target was repointed at it) and
it recorded the stream after the `CONNECT` reply:

```
01 01 c0a84d04 753a 0084 57464531 ...
│  │  │        │    │    └── first frame begins: magic "WFE1"
│  │  │        │    └────── u16be frame length = 132
│  │  │        └─────────── port 30010
│  │  └──────────────────── 192.168.77.4
│  └─────────────────────── ATYP 0x01 = IPv4 (RFC 1928)
└────────────────────────── isConnect = 1 (UoT v2 connect mode)
```

That is exactly the layout sing-box's own reader expects. `sing/common/uot/protocol.go` at the
revision sing-box v1.14.2 pins (`sing v0.9.6-0.20260922013354-87c33f17688f`) reads:

```go
func ReadRequest(reader io.Reader) (*Request, error) {
	var request Request
	err := binary.Read(reader, binary.BigEndian, &request.IsConnect)   // 1 byte
	request.Destination, err = M.SocksaddrSerializer.ReadAddrPort(reader) // SOCKS ATYP 0x01/0x04/0x03
	...
}
```

So the bytes are not the problem.

## The trigger is pipelining, and it is sing-box's defect

A hand-written UoT client was driven against the same sing-box from the same machine, twice:

| client behaviour | result |
|---|---|
| send greeting, read reply; send `CONNECT`, read reply; **then** send the UoT request | `inbound UoT connect connection to 192.168.77.4:30010` — **works** |
| send greeting + `CONNECT` + UoT request + first frame **in one write** | `UoT read request: EOF` (or, when more datagrams follow, `unknown address family: 152`) — **fails** |

`0x98 = 152` is the low byte of a 152-byte frame's `u16be` length prefix, which is the frame size of
a 120-byte payload — i.e. the reader had skipped the entire 8-byte request header and started
reading at the next datagram's length prefix.

The cause is visible in `protocol/socks/inbound.go` of sing-box v1.14.2:

```go
// line 75 — the handshake is parsed through a buffered reader
err := socks.HandleConnectionEx(ctx, conn, std_bufio.NewReader(conn), h.authenticator, ...)
...
// line 92 / 97 — but the router is handed the RAW connection
h.router.RouteConnectionEx(ctx, conn, metadata, onClose)
```

Any bytes the `bufio.Reader` pulled past the end of the SOCKS5 request stay in the buffer and are
never seen again. When the client pipelines the UoT request behind the `CONNECT` — which is a legal
thing to do on a byte stream, and which WinForward does deliberately, writing `CONNECT` + UoT
request + first datagram as one buffer to save a round trip — the request header is swallowed and
the UoT router parses whatever the client sends next.

## Version range

Both checked builds are affected, so this is not a recent regression:

| sing-box | pipelined UoT request | handshake-then-request |
|---|---|---|
| 1.12.23 | fails | works |
| 1.14.2 | fails | works |

(The 1.13.0 and 1.14.0 Linux binaries could not be executed on this NixOS host because they are
dynamically linked against a generic loader; the source path in v1.14.2 is unchanged from the
revision that v1.12.23 pins, so the same code is on both ends of that gap.)

## Consequence for the benchmark

`udpOverTcp: true` cannot be measured end-to-end against sing-box: every UDP row for WinForward AOT
and FDD would read "100 % loss", which measures the interoperability failure rather than the
product. Two things are therefore recorded separately:

1. The main matrix runs WinForward with the native per-flow `UDP ASSOCIATE` relay, so its UDP
   numbers are comparable with ProxiFyre and ProxyBridge.
2. This failure is reported as its own finding, with the byte capture, the hand-written client
   experiment and the sing-box source line as evidence.

## For the product

Relevant to whoever owns the UoT transport: the client side is correct and the failure is on the
server side, so the fix is either an upstream sing-box report, or — if interoperation has to be
robust against servers that buffer — waiting for the SOCKS5 `CONNECT` reply before writing the UoT
request, at the cost of one round trip on every UDP flow's first datagram.
