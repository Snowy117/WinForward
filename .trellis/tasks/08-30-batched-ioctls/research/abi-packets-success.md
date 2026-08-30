# ABI verification — `EthernetMultiRequest.PacketsSuccess` semantics on the batched send path (S2)

> Question (implement.md S2 / design D1): does `SendPacketsToAdapter` / `SendPacketsToMstcp`
> report a per-packet success count through `ETH_M_REQUEST.dwPacketsSuccess` (prefix count,
> bitmask, or undefined)? The D1 branch — suffix resend vs fail-the-batch — depends on it.
>
> **Finding: `dwPacketsSuccess` is NEVER communicated back on the send path. The batched send
> APIs are all-or-nothing (BOOL). Evidence is unambiguous, so D1 takes the documented
> fail-the-batch branch: a failed batch throws with today's single-send fail-fast semantics;
> there is no partial-success surface to handle.**

Evidence, pinned to the same upstream commit as the ABI verification in
`.trellis/spec/backend/windows-ndisapi.md` (`wiresock/ndisapi` @ `417b8734e844083a10236387fba705d94a2d6bc9`),
plus a disassembly of the real `ndisapi.dll` shipped beside the smoke harness
(`smoke/ndisapi.dll`, PE32+ x64; export table matches the pinned `ndisapi.def` names).

## 1. User-mode source: the send IOCTL passes no output buffer

`ndisapi/ndisapi.cpp`, `CNdisApi::SendPacketsToMstcp` (x64 native path, ~line 758) and
`CNdisApi::SendPacketsToAdapter` (x64 native path, ~line 765) — both identical in shape:

```cpp
bIOResult = DeviceIoControl(
    IOCTL_NDISRD_SEND_PACKETS_TO_MSTCP,          // ..._TO_ADAPTER in the sibling
    pPackets,
    sizeof(ETH_M_REQUEST) + sizeof(NDISRD_ETH_Packet) * (pPackets->dwPacketsNumber - 1),
    NULL,                                        // lpOutBuffer
    0,                                           // nOutBufferSize
    NULL,   // Bytes Returned
    NULL
);
```

Contrast `CNdisApi::ReadPackets` (~line 872), which passes the request as BOTH input and
output and on the WOW64 path explicitly copies the driver-filled field back:

```cpp
bIOResult = DeviceIoControl(IOCTL_NDISRD_READ_PACKETS, pEthRequest, size,
                            pEthRequest, size, NULL, NULL);   // request is also the OUT buffer
if (bIOResult)
{
    pPackets->dwPacketsSuccess = pEthRequest->dwPacketsSuccess;
    ...
}
```

So the read path deliberately round-trips `dwPacketsSuccess`, and the send path
deliberately does not.

## 2. Header: the send IOCTLs are METHOD_BUFFERED

`include/Common.h` (~line 960):

```c
#define IOCTL_NDISRD_SEND_PACKETS_TO_ADAPTER\
   CTL_CODE(FILE_DEVICE_NDISRD, NDISRD_IOCTL_INDEX+20, METHOD_BUFFERED, FILE_ANY_ACCESS)
#define IOCTL_NDISRD_SEND_PACKETS_TO_MSTCP\
   CTL_CODE(FILE_DEVICE_NDISRD, NDISRD_IOCTL_INDEX+21, METHOD_BUFFERED, FILE_ANY_ACCESS)
```

With `METHOD_BUFFERED`, the I/O manager copies the system buffer back to the caller only up
to `nOutBufferSize`. The DLL calls with `lpOutBuffer = NULL, nOutBufferSize = 0`, so nothing
in the request structure can change across the call — even if the kernel driver wrote a
success count into its system-buffer copy, it can never reach user mode.

The shared header documents the field generically (`include/Common.h` ~line 287:
"`dwPacketsSuccess` This field stores the number of successfully processed packets"), but
only the read direction has a return channel; the doc comment for both send functions says
only "`@return BOOL Returns TRUE if the operation is successful, or FALSE otherwise" — no
per-packet accounting.

## 3. Real DLL disassembly (smoke/ndisapi.dll)

Export RVAs (resolved from the export table): `?SendPacketsToMstcp@CNdisApi@@QEBAHPEAU_ETH_M_REQUEST@@@Z`
@ RVA 0x16c0, `?SendPacketsToAdapter@CNdisApi@@...` @ RVA 0x1720. Both decode identically
(shown for MSTCP; the Adapter twin differs only in the immediate IOCTL code):

```asm
1800016c0: sub    $0x48,%rsp
1800016c4: mov    0x8(%rdx),%eax            ; eax = pPackets->dwPacketsNumber
1800016ca: mov    0x30(%rcx),%r10           ; r10 = this->hDriver
1800016ce: lea    0x10(,%rax,8),%r9d        ; r9d = 16 + 8*N  (nInBufferSize)
1800016f1: mov    %rdx,%r8                  ; r8 = pPackets   (lpInBuffer)
1800016f9: mov    $0x83002114,%edx          ; IOCTL_NDISRD_SEND_PACKETS_TO_MSTCP
1800016fe: mov    %r11,0x20(%rsp)           ; 5th arg (lpOutBuffer)  = r11 = NULL
1800016f4: mov    %r11d,0x28(%rsp)          ; 6th arg (nOutBufferSize) = 0
180001703: call  *0x49af(%rip)              ; kernel32!DeviceIoControl
```

- `0x83002114` (MSTCP) / `0x83002110` (Adapter): low two bits `00` = `METHOD_BUFFERED`,
  functions differ by exactly 1 (`+21` vs `+20`), matching the pinned header.
- The 5th/6th stack argument slots (`0x20(%rsp)`, `0x28(%rsp)`) are explicitly zeroed from
  `r11` (`xor %r11d,%r11d` earlier): **lpOutBuffer = NULL, nOutBufferSize = 0**, confirming
  the pinned source is the code actually shipped in the DLL we drive.

## Decision (D1 branch)

Suffix resend is impossible to implement on this ABI — there is no per-packet success
signal to key it on. `NdisApiDriver.SendPacketsToMstcp/ToAdapter(nint, NdisPacketBuffer[], int)`
therefore:

- builds `EthernetMultiRequest` chunks (≤ `(1024 - 16) / 8` slots each, inside the existing
  1KB stackalloc budget) via the shared `BuildMultiRequest` (with an offset parameter);
- holds ONE adapter-gate lease across the whole flush;
- ignores `PacketsSuccess` entirely (it stays 0 as built; nothing can write it back);
- on a FALSE native result, throws `Win32Exception` including native error, chunk range,
  total count, direction, and adapter handle — the same fail-closed propagation the
  single-packet path has today, so a batch failure still stops the run and restores adapter
  modes instead of silently dropping part of a batch.

The `InterpretBatchReadResult` clamp discipline remains read-path-only; there is no
send-path analogue to clamp.
