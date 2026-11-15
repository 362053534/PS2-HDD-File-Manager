# APANET-DIAG-2 network stall investigation

This revision adds observations, not a fix for the reproduced stall. It keeps
protocol, buffers, priorities of existing threads, timeouts and retry policy.
The new low-priority (0x78) monitor refreshes eighteen fixed screen rows (13–30) about once
per second; it adds 12 KiB of thread stack and small counters. Instrumentation
and screen updates can affect timing, so a successful run does not prove a fix.

Build through the existing Actions PS2 ELF job. The output remains the stripped,
ps2-packer-compressed `ApaNetServer.ELF`; no workflow changes are required.
`APANET-DIAG-2` is appended outside the packed loading segments for identification.
Verify `DIAG-2 HB:` appears on the PS2 screen before testing. No client update
is needed for this diagnostic revision.

## Test

1. Close the GUI client. Boot this ELF and retain `+OPL:/TEST-WIFI`.
2. Run the existing 6 GiB probe with host packet capture. Do not run another
   client at the same time. Keep the previous failed file until inspected.
3. At a stall, record a short video or photos of the diagnostic rows, including
   two samples at least 10 seconds apart. Leave the PS2 powered on; do not press
   START or reconnect until host captures and the screen state are preserved.
4. Stop at the first failure. Do not delete or automatically retry failed uploads.
   Only delete an exact-size, committed file created by the test itself.

## Screen fields

- HB/up: monitor scheduling and local hardware timer. A frozen HB does not alone
  distinguish thread starvation, disabled interrupts, timer waits or a CPU hang.
- PUT/banks: receiving PUT phase and configured ring capacity. PUT=0 is set after
  socket/file close, before final commit; it does not certify import success.
- RX/age: packets passed to the EE protocol stack; age is seconds since the
  sampled count last advanced. SIF counts this program's SIF0 interrupt hook,
  including non-network RPC traffic. RX stage is sleep/alloc/dma-clear/enqueue.
- pbuf-miss: first allocation failures, including ones recovered by expansion.
  alloc-retry counts unsuccessful NETMAN replacement allocations.
- clock: calls to lwIP sys_now across all callers, not a tcpip thread heartbeat.
- msg/peak: shared message records in use, of 1024. calls counts blocking allocator
  invocations, including calls immediately satisfied. waiting counts invocations
  currently inside that allocation wait; it is not a cumulative failure count.
- NET/HDD: received and written MiB (64-bit byte counters), including resume
  offset. Ages are sampled no-progress durations while PUT is active. They are
  observations, not deadlines. NET err is recv errno (EOF has err=0); HDD err is
  the raw fileXioWrite result. Stages distinguish waiting for ring space/data,
  recv, file write and close. `done` means receive/write phase complete only.
- BD: current EE RX descriptor index and current/next lengths, RX busy flag,
  pending SIF command size, raw SIF0 CHCR and DMAC D_STAT. Read from EE memory
  and registers; the monitor never makes IOP RPC or network calls.

Counters are intended for fault localization, not a benchmark or exact packet
accounting. Some hot-path counters may wrap or lose increments between contexts.
No persistent logs are written to PS2 storage, and diagnostics cannot survive a
power cycle without a screen recording. A stall can still require a second,
more targeted diagnostic build after these observations narrow the cause.

## Validation limits

Native Mac seam checks compile the actual diagnostics module with SDK function
stubs, exercise progress above 4 GiB, error recording and thread start failures,
and verify the bounded screen output. Existing partition failure tests pass.
These checks do not validate MIPS linkage or PS2 hardware behavior; the canonical
362053534 image in Actions must build the ELF, followed by the real PS2 test.

## DIAG-2 additions after the 2026-11-14 fault

DIAG-1 reproduced the fault at client offset 4,808,245,248 bytes. The host
reported ETIMEDOUT followed by Broken pipe. PS2 HB continued, pbuf-miss and
alloc-retry were zero, the message pool was empty, RX was sleeping, and NET/HDD
both showed 4585 MiB with the receive/write phase ended. These observations do
not identify a specific defect or exclude an earlier transient resource stall.

DIAG-2 adds LIVE IP/DHCP state from the EE-local ps2ip_getconfig implementation
(no IOP RPC), so a changed address can be distinguished from a stalled data path.
TX req counts transmit notifications, poll counts driver next-packet calls, deq
counts completed driver dequeue calls, and last is the last polled packet size.
These are different kinds of events; their differences are not queue depth and
do not prove packets reached the cable. They can help distinguish stack/driver
progress from missing responses at the host.

TX/CORE/RX thread rows report kernel status, wait type/id and pending wakeups.
Roles are identified by the canonical stack's initial priorities 0x56/0x58/0x59;
if SDK priorities change, identification must be checked. state 1=running,
2=ready, 4=waiting; wait 1=sleep, 2=semaphore. These are local kernel reads only;
they never wake, suspend or reprioritize threads. All eighteen rows end at row 30,
within the SDK debug screen's 80x40 character bounds. TV overscan still needs
visual verification. New counters and rows can affect reproduction timing.

SNAP rows retain the most recently detected interval with both received and
written byte counts unchanged for at least 10 seconds during an active PUT.
They remain visible after socket cleanup resets live stages. SNAP is explicitly
a historical sample, not current state or proof of failure: a temporary pause
may also trigger it. A later qualifying stall replaces it after progress resumes.
LIVE/HB/TX and thread rows continue updating normally. Nothing automatically
recovers, aborts or changes transfer behavior in response to this observation.

SNAP TX/CORE/RX retain the thread status sampled in that same monitor iteration.
This prevents a later connection cleanup from erasing the observed wait states.
Status 0 and tid -1 mean the thread could not be identified/read, not a CPU state.

The subsequent 2026-11-15 postfault ARP/ICMP checks passed (four ARP and four raw
ICMP replies, then 3/3 ordinary ping replies). The user confirmed PS2 was rebooted
in between; these results show post-reboot reachability, not spontaneous recovery
or proof that ARP refresh fixes the fault.
