# Host UDP A4 failure — reply source is not preserved

Date: 2026-10-26
Environment: WinLTSC 26100, test-signed `wfprobe` driver (SHA-256
`3E30CDF3D1BF03D54178B17605D7207B2F39995C75F06438393158CF487A1423`), Kernel provider,
framework-dependent CLI, sing-box `127.0.0.1:1080`, Linux TCP/UDP targets at
`192.168.100.4:40001/40010/40011/40012/40013`.

## Result

The host UDP proxy data path works (association → shared coordinator → SOCKS5 UDP ASSOCIATE →
sing-box → target → reply), but the reply reaches the client with the **listener's source
endpoint**, not the original destination:

```text
PORT=40010 TRY=0 RESULT=OK FROM=192.168.100.2:59077 DATA=hello-udp-40010-0
PORT=40010 TRY=1 RESULT=OK FROM=192.168.100.2:59077 DATA=hello-udp-40010-1
PORT=40010 TRY=2 RESULT=OK FROM=192.168.100.2:59077 DATA=hello-udp-40010-2
PORT=40011 TRY=0 RESULT=OK FROM=192.168.100.4:40011 DATA=hello-udp-40011-0
PORT=40013 TRY=0 RESULT=TIMEOUT ...
```

The pass arm (40011, direct target) correctly shows `FROM=192.168.100.4:40011`. The block arm
(40013) times out. The target log shows the proxied datagrams arriving at `192.168.100.4:40010`
(the proxy leg ran); sing-box shows the relay to the target.

This violates the host UDP acceptance (A4): the client must observe its original destination as
the reply source. An unconnected client sees the listener; a connected UDP client is expected to
drop the reply because its peer is the original destination. The same listener-socket `sendto`
design was the research assumption (`research/host-udp-driver.md` §3.3) and is now disproven.

## Raw-socket mitigation is not viable

A userland raw IPv4 packet (`IP_HDRINCL`) was tested on the VM:

- source `192.168.100.2` (local), destination `192.168.100.2`, client port: `RAW-OK FROM=192.168.100.2:40010`.
  The raw construction/checksum and local delivery path work.
- source `192.168.100.4` (the original destination, remote on-link), destination `192.168.100.2`:
  `RAW-FAIL` (client receive timeout), both with the Windows firewall profiles enabled and with all
  three profiles disabled. The Windows stack does not deliver a local spoofed-source raw packet.

Raw socket source spoofing is therefore not a usable response leg.

## Required path

The reply must be injected or source-rewritten by the Kernel driver (or another privileged packet
path) so the local client receives a UDP datagram whose source is the original destination
(`Flow.Remote`). The existing `HostUdpResponseSink` (listener `sendto`) is provisional; the
baseline code must not be committed as accepted until the reply-source batch passes A4/A8 for
unconnected and connected IPv4, dual-mode and native IPv6.

## Evidence files

- `a4-stats.txt` — driver counters after the proxy/pass/block clients (`udp_calls=15`,
  `udp_redirected=2`, `udp_blocked=1`; the proxy leg ran).
- `a4-wf.err.log` — Kernel CLI startup/ready lines (listener 59077, host UDP enabled, cap 4096).
- `a4-singbox.log` — SOCKS inbound/outbound evidence.
- `a4-target.log` — Linux target receives for 40010/40011.
- `a4-cleanup.txt` — driver config zeroed, tasks/processes removed, VM network intact.

## State

The 6a working-tree implementation remains uncommitted. The driver config is zeroed and the VM is
clean. Follow-up: `10-26-host-udp-reply-source`.
