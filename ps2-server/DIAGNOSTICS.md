# APANET-DIAG-3: bounded data I/O and network-stall investigation

This revision adds an application-level data inactivity deadline and corrects
incomplete diagnostic coverage. It does not claim to fix the original packet
loss, prove its hardware/software cause, or improve transfer speed.

## Recovery behavior

The old code set SO_RCVTIMEO/SO_SNDTIMEO to 90 seconds without checking whether
the SDK accepted them, then used blocking recv/send. The DIAG-2 photo showed no
received data for about 359 seconds while still inside recv, with the disk
thread waiting for another filled bank. The effective 90-second deadline was
not enforced in that case. The exact underlying SDK failure is not yet proven.

`data_io.c` now temporarily enables nonblocking mode on the dedicated data socket,
attempts send/recv immediately, and only yields when the socket would block.
A monotonic, wrapping-safe clock enforces 90 seconds without actual byte progress.
Progress resets the inactivity deadline; total transfer duration is not limited.
Cancellation is checked on each iteration. Original fd flags are restored before
the caller closes the socket. Optional socket timeouts remain best effort and
their success is shown as SOtimeo (bit 1=receive, bit 2=send).

Partial receive progress is counted but an incomplete ring bank is never returned
as successful. Existing partial-file/resume, PFS synchronization and PUT_END
commit paths remain in charge of cleanup. This is not a promise of a hard
realtime deadline if a lower-level SDK call, timer or scheduler itself hangs.
The nominal 1ms polling yield is subject to PS2 timer resolution and scheduling;
its throughput effect must be measured on hardware.

## Build and startup check

Use the existing Actions PS2 ELF job and the owner 362053534 toolchain image.
Output remains the stripped, ps2-packer-compressed `ApaNetServer.ELF`, with an
`APANET-DIAG-3` marker outside packed loading segments. No client update is needed.

Before a long transfer, confirm on the real PS2:

1. `DIAG-3 HB:` advances and LIVE IP is the expected target.
2. `OUT hook:1` appears. After host ping, OUT calls/done should advance.
3. RX shows the actual nonnegative thread ID and a readable kernel status.

If these checks fail, preserve the screen and inspect the build/probes before
another long upload. DIAG-2's zero TX wrapper counters and missing TX/RX thread
lookups were not valid proof of dead threads. Native stub tests did not validate
those probes in the linked PS2 runtime, so their missing coverage is corrected.

## Screen interpretation

Nineteen fixed rows (13–31) refresh about once per second, without scrolling.
Each row is bounded to 78 characters within the SDK's 80x40 character screen;
TV overscan still needs visual confirmation. No PS2 disk logs are written.

- HB/up: monitor scheduling and local timer; frozen HB alone cannot distinguish
  starvation, disabled interrupts, timer wait failure or a CPU hang.
- PUT: receive/write phase, not successful commit. It can become 0 before PUT_END.
- RX/age: frames passed to the EE protocol stack and time since sampled progress.
  SIF counts this program's SIF0 hook, including unrelated RPC traffic.
- pbuf-miss includes first failures recovered by expansion. alloc-retry counts
  NETMAN replacement-buffer failures. Zero now does not rule out an earlier stall.
- msg/peak: shared message records used, of 1024. calls counts blocking allocator
  invocations (even immediately satisfied); waiting is current allocation waits.
- NET/HDD: actual received/written MiB including resume offset, stages and errors.
  Ages are sampled no-progress durations while PUT is active, not deadlines.
- BD: current/next RX descriptor lengths and raw EE SIF0/DMAC register state.
- LIVE IP/DHCP: EE-local interface configuration, sampled without IOP RPC.
- OUT: actual netif linkoutput observer. calls/done bracket the original callback;
  err is its most recent result. These events do not prove transmission on wire.
  queue is the length of the current NETMAN TX head packet, not total queue bytes.
  It is read through the canonical non-consuming Next API. hook indicates that
  the function pointer is still attached.
- OUT thread is the latest output producer, not the NETMAN hardware TX worker.
  RX uses the real thread ID already stored by this program. CORE uses the
  canonical initial priority 0x58 lookup; unavailable never means dead.
  Kernel state 1=running, 2=ready, 4=waiting; wait 1=sleep, 2=semaphore.
- RING pending counts all nonempty EE RX descriptors; first is an index or -1.
  NMlink is the NETMAN event state, not an independent PHY hardware poll. flags
  are the live netif flags. SOtimeo reports whether optional socket options worked.

SNAP rows retain the latest interval with both byte counts unchanged for at least
10 seconds during active PUT, together with output counters and thread states.
They remain visible after cleanup. SNAP is historical, not live or proof of a
fault: a temporary pause may trigger it. A subsequent qualifying stall replaces
it after progress resumes. Counters may wrap or lose cross-context increments.

## Reproduction

Close the GUI. Use only the 6 GiB probe and `+OPL:/TEST-WIFI`. Capture traffic
before connection. After an acknowledged PUT_END, verify exact filename and size
before deleting only the probe's own file and repeating. Size checks do not prove
content checksums. Stop before retries at the first failure, preserve failed
staging, and record the entire diagnostic region. Do not reboot/reconnect until
host captures and screen evidence are saved. Record any intervening reboot so
post-reboot reachability is never mistaken for spontaneous recovery.

## Validation

The actual old recv/tuning path was reproduced as blocking with a silent open
peer when socket timeout options are rejected. Native socket tests now check
silent/partial receive, progress resetting the deadline, cancellation, EOF,
normal send, non-reading peer backpressure, clock wrap and fd flag restoration.
The real output-observer module is tested through a function pointer for exact
packet/result forwarding, idempotency, attachment loss and non-consuming peek.
The monitor seam checks >4GiB counters, start failures, bounded rows and retained
snapshots after live cleanup. Partition failure safety tests also pass.

Actions runs the socket/output/partition tests before compiling the ELF. These
native checks use SDK-boundary stubs and cannot validate MIPS linkage or actual
PS2 stack internals; Actions build and the startup checks plus real transfer are
still required. Instrumentation and extra screen output may change fault timing.
