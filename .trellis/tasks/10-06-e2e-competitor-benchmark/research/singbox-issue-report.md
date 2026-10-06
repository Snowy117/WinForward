# Draft upstream issue report (for the user to file — not filed by this task)

Target repository: `SagerNet/sing-box` (the defect is in `SagerNet/sing`, reached through sing-box's
SOCKS inbound). Suggested title:

> **socks inbound: a payload pipelined behind the request is lost, which breaks UoT v2 clients**

---

## Environment

- sing-box 1.14.2 (Windows x64 official release), server side also reproduced as the `socks`
  inbound on Linux.
- Client: a third-party proxifier that speaks SOCKS5 and UoT v2.
- `sing` revision pinned by v1.14.2: `v0.9.6-0.20260922013354-87c33f17688f`.

## Description

A SOCKS5 client is allowed to put its first payload bytes in the same TCP segment as the request —
on a byte stream there is no rule against it, and clients that do not want to pay a round trip per
flow do it. sing-box loses those bytes.

The visible symptom for a UDP-over-TCP v2 client is that **every** UoT flow fails at the request
header, because the client writes the `CONNECT` to `sp.v2.udp-over-tcp.arpa`, the UoT request
header and the first datagram frame as a single buffer:

```
INFO  inbound/socks[in]: inbound connection to sp.v2.udp-over-tcp.arpa:0
ERROR inbound/socks[in]: process connection from 127.0.0.1:58042: UoT read request: unknown address family: 152
```

`0x98 = 152` is the low byte of the `u16be` length prefix of a 152-byte frame (a 120-byte payload),
so the reader had skipped the entire 8-byte request header and started at the datagram's length
prefix. When no further datagram follows, the same flow reports `UoT read request: EOF` instead.

The same loss applies to plain CONNECT: any application that pipelines its first request bytes
behind the SOCKS5 request has them dropped.

## Reproduction

No proxy software is needed. Everything goes out in **one** `sendall`, which is the whole point:

```python
import socket, struct, time

MAGIC = b'sp.v2.udp-over-tcp.arpa'
greeting = bytes([5, 1, 0])
connect = bytes([5, 1, 0, 3, len(MAGIC)]) + MAGIC + b'\x00\x00'
uot_request = bytes([1, 1, 192, 168, 1, 1, 0x75, 0x3A])   # isConnect, ATYP=IPv4, addr, port
frame = struct.pack('>H', 8) + b'WFE1\x00\x00\x00\x01'

s = socket.create_connection(('127.0.0.1', 1080))
s.sendall(greeting + connect + uot_request + frame)       # <- one write
time.sleep(1)
s.close()
```

With a `socks` inbound and a `direct` outbound:

| client behaviour | sing-box log |
|---|---|
| send greeting, read reply; send `CONNECT`, read reply; **then** send the request | `inbound UoT connect connection to 192.168.1.1:30001` — works |
| send everything **in one write** | `UoT read request: EOF`, or `unknown address family: N` when more datagrams follow — fails |

The request header itself is not the problem: it is byte-for-byte what
`sing/common/uot/protocol.go` reads (`isConnect`, then a SOCKS address). Capturing the stream on the
wire shows `01 01 c0a80101 753a 0084 57464531 …`, and a hand-written client that waits for the
`CONNECT` reply before writing it is accepted.

## Root cause

`protocol/socks/inbound.go` (v1.14.2):

```go
func (h *Inbound) NewConnection(ctx context.Context, conn net.Conn, metadata adapter.InboundContext, onClose N.CloseHandlerFunc) {
	err := socks.HandleConnectionEx(ctx, conn, std_bufio.NewReader(conn), h.authenticator, ...)
```

and in `sing/protocol/socks/handshake.go`, the handler receives the **raw** connection:

```go
case socks5.CommandConnect:
	handler.NewConnectionEx(ctx, NewLazyConn(conn, version), source, request.Destination, onClose)
```

The handshake reads through the `bufio.Reader`, and `bufio` fills its whole buffer on every fill, so
everything the peer already sent past the request is in that reader. Passing `conn` onward discards
it. The bytes are not recoverable from the socket because they were consumed; the handler reads
whatever the peer sends next.

## Suggested fix

Hand the handler a connection whose reads drain the reader first. A minimal patch against
`sing/protocol/socks/handshake.go` adds a small wrapper and applies it at both CONNECT sites (SOCKS4
and SOCKS5):

```go
type handshakeConn struct {
	net.Conn
	reader *std_bufio.Reader
}

func newHandshakeConn(conn net.Conn, reader *std_bufio.Reader) net.Conn {
	if reader.Buffered() == 0 {
		return conn
	}
	return &handshakeConn{Conn: conn, reader: reader}
}

func (c *handshakeConn) Read(p []byte) (n int, err error) {
	if c.reader.Buffered() > 0 {
		return c.reader.Read(p)
	}
	return c.Conn.Read(p)
}

func (c *handshakeConn) WriteTo(w io.Writer) (n int64, err error) { /* drain reader, then conn */ }
func (c *handshakeConn) ReaderReplaceable() bool { return c.reader.Buffered() == 0 }
func (c *handshakeConn) WriterReplaceable() bool { return true }
func (c *handshakeConn) Upstream() any           { return c.Conn }
```

followed by `NewLazyConn(newHandshakeConn(conn, reader), version)` at both call sites.

An equally acceptable alternative is to stop over-reading in the handshake: every SOCKS5 field is
either fixed-size or length-prefixed, so it can be read with exact-size reads and no buffering.

A regression test is included with the patch:

```go
func TestPipelinedRequestPayloadReachesHandler(t *testing.T)
```

It fails before the change with `handler received "", want "PIPELINED": the handshake reader
swallowed the pipelined payload` and passes after it.

## Attachment

`sing-socks-pipelined-payload.patch` — one commit against `sing` at
`87c33f17688f` (the revision sing-box v1.14.2 pins), with the test.
