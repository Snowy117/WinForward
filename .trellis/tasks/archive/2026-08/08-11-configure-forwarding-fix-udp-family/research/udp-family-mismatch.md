# UDP Address-Family Mismatch Research

## Exact Reproduction

Before the fix, the throwaway reproduction project was run with:

```bash
dotnet run --project .trellis/tasks/08-11-configure-forwarding-fix-udp-family/research/repro/WinForwardUdpFamilyRepro.csproj -c Release
```

Output:

```text
Flow endpoints must use the same address family. (Parameter 'remote')
```

The throwaway project was deleted after its scenario became product regression
coverage; this command is historical evidence, not a current validation step.

The reproducer uses the real `UdpProxyCoordinator` and a fake
`IUdpProxyTransport` that represents the observed setup: IPv6 local UDP
endpoint and IPv4 SOCKS5 relay endpoint. `UdpProxyCoordinator.CreateSessionAsync`
passes both endpoints to `FlowKey.Create`, which deterministically throws the
same exception logged by the user.

## Root Cause

`UdpProxyCoordinator` asks the transport factory for a socket based on the
original flow's local family. `Socks5UdpTransport` creates and binds that socket
before it sends UDP ASSOCIATE. Its control connection can reach an IPv4 SOCKS5
server such as `127.0.0.1:30890`, and UDP ASSOCIATE can return or normalize to
an IPv4 relay. The resulting IPv6-local/IPv4-relay alias violates `FlowKey`'s
same-family tuple invariant.

This occurs before a SOCKS5 UDP payload is sent or received. It is therefore
not evidence that the user's SOCKS5 UDP server is broken.

## Protocol Evidence

RFC 1928 section 7 says that a client sends UDP frames to the BND endpoint
returned by UDP ASSOCIATE. Every frame carries a separate `ATYP`, `DST.ADDR`,
and `DST.PORT` header. The relay transport endpoint family and destination ATYP
are independent. RFC 1928 also requires all-zero address and port in UDP
ASSOCIATE when the client does not yet know its UDP endpoint.

Source: https://datatracker.ietf.org/doc/html/rfc1928

## Hypotheses Tested

1. **The SOCKS5 server has a UDP protocol failure.** Rejected: the exact
   exception arises locally in `FlowKey.Create` before any UDP send.
2. **The IPv6 original flow forces an IPv6 relay socket.** Confirmed as the
   implementation bug: coordinator selects that family and the concrete
   transport allocates before the relay is known.
3. **An IPv4 relay cannot carry an IPv6 destination.** Rejected by RFC 1928 and
   `Socks5UdpCodec.Encode`, which independently writes IPv6 ATYP/destination.
4. **The fix should use the control peer family instead of the relay family.**
   Rejected: the client must send to the BND endpoint; an explicit BND endpoint
   can differ from the control peer.

## Selected Fix

Open/authenticate control, send an all-zero UDP ASSOCIATE in the control family,
then allocate and bind a UDP socket in the returned relay's family. Continue to
encode the original destination unchanged in each SOCKS5 UDP frame. Keep
loop-prevention registration and every setup failure fail-closed.

## Configuration Result

The recommended configuration is `recommended-config.json`. It uses the target
machine's Network Bridge GUID, proxies that adapter's forwarded traffic, and
leaves every other forwarded adapter outside the proxy. Its process/CIDR host
exemptions appear before host proxy rules.
