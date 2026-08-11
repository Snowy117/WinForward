# Implementation Plan

## Checklist

- [x] Decouple `IUdpProxyTransportFactory` and both concrete transport factory
      methods from the original flow's address family.
- [x] Add a control-family all-zero UDP ASSOCIATE operation and use it before
      allocating a UDP socket.
- [x] Make `Socks5UdpTransport` allocate/bind the UDP socket from the returned
      relay address family, preserving control and UDP self-traffic ownership
      and cleanup ordering.
- [x] Remove the coordinator's original-flow-to-UDP-socket-family selection;
      retain same-family `RelayAlias` construction, association claim, capacity,
      and fail-closed behavior.
- [x] Replace the test that asserts an IPv6 original flow forces an IPv6 relay
      socket with a regression covering an IPv6 original flow and IPv4 local/
      relay transport.
- [x] Add a loopback SOCKS5 UDP ASSOCIATE integration test that validates an
      all-zero associate request, IPv4 relay transport, and IPv6 destination
      encoded in the received UDP frame.
- [x] Delete the task-local throwaway reproduction project after its scenario
      is locked into product regression tests; retain the captured command and
      output in the research report.
- [x] Update control setup failure/cleanup tests for the deferred socket
      creation order, and retain matching-family, source-validation,
      loop-prevention, association reuse, and capacity tests.
- [x] Add the recommended bridge configuration under task research and validate
      it with `WinForward validate` or configuration tests.
- [x] Update README to document relay-family versus destination-ATYP behavior
      and the stable-ID bridge selection example if appropriate.
- [x] Run build, complete tests, formatting, whitespace, and a Trellis quality
      check.

## Validation

```bash
dotnet build -c Release
dotnet test -c Release
dotnet format --verify-no-changes
git diff --check
```

## Review Gates

- The revised transport must not create a UDP socket before it knows the relay
  address family.
- UDP ASSOCIATE uses an all-zero address/port in the TCP control family, not
  the original destination family.
- UDP relay traffic remains registered before the first relay datagram leaves.
- Proxy-selected failures remain blocked; no silent direct fallback is added.
- The recommended configuration uses only the bridge GUID, not `adapterName`.

## Verification Result

- Focused SOCKS5 control and UDP tests: passed, 46/46.
- `dotnet build -c Release`: passed with 0 warnings and 0 errors.
- `dotnet test -c Release --no-build`: passed, 245/245 tests.
- Changed-file `dotnet format --verify-no-changes`: passed.
- `git diff --check`: passed.
- Task context manifests and recommended JSON configuration: passed validation.
- Repository-wide format remains affected by pre-existing newline/import issues
  in untouched files.

## Rollback Points

- Revert the transport factory, control association, and coordinator invocation
  changes as one unit; no configuration migration is required.
- Keep regression tests and documentation aligned with the selected behavior.
