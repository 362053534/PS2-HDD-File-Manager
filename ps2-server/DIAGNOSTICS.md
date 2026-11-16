# APANET-DIAG-8: bounded data I/O and network-stall investigation

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
`APANET-DIAG-8` marker outside packed loading segments. No client update is needed.

Before a long transfer, confirm on the real PS2:

1. `DIAG-8 HB:` advances and LIVE IP is the expected target.
2. `OUT hook:1` appears. After host ping, OUT calls/done should advance.
3. RX shows the actual nonnegative thread ID and a readable kernel status.

If these checks fail, preserve the screen and inspect the build/probes before
another long upload. DIAG-2's zero TX wrapper counters and missing TX/RX thread
lookups were not valid proof of dead threads. Native stub tests did not validate
those probes in the linked PS2 runtime, so their missing coverage is corrected.

## Screen interpretation

Twenty fixed rows (zero-based 6–25) refresh about once per second, without scrolling.
Each row is bounded to 70 characters in columns 4–73, leaving TV overscan margins;
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
- IO thread is the actual transfer caller, sampled through GetThreadId; it is
  independent of the output producer whose callbacks OUT counts.
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

## DIAG-4 SDK requirement and hardware validation

DIAG-4 requires the owner 362053534 SDK EE network library rebuilt with
LWIP_SOCKET_SET_ERRNO=1. Its libc socket adapters depend on errno being set
when a raw lwIP operation fails. The previous configuration could turn an
errno-disabled would-block result into a false zero-byte receive. DIAG-3's
new nonblocking loop exposed that mismatch and introduced an immediate upload
failure; this was a regression introduced by the application change.

The user confirmed that the SDK/toolchain image was updated. The temporary
application error-code wrappers and their dedicated test have been removed.
The program now uses the SDK's socket error contract directly. The actual
nonblocking data loop, inactivity deadline, cancellation and network probes
remain unchanged; no macOS or Windows client update is needed.

The SDK change must be compiled into libps2ip.a installed in the image:
changing headers alone cannot update a precompiled library. The existing
Actions job pulls ghcr.io/362053534/ps2homebrew:main and performs a clean
Release ELF build with stripping and ps2-packer compression.

Native data-loop, output-observer and partition-safety checks do not prove the
new image's MIPS runtime behavior. After building, confirm the DIAG-4 startup
probes, initial upload, and repeated 6 GiB transfers on the real PS2. The
original intermittent long-transfer fault, effective 90-second recovery and
throughput remain unverified; this change does not claim they are fixed.

## DIAG-5: locate the stalled data operation without changing behavior

DIAG-4 round 1 at 2026-11-15 16:36 +08:00 stopped after PS2 TCP cumulatively
acknowledged 464115 bytes. It kept replying with window zero and answered ping.
The client timed out after 120 seconds and froze before retry. The screen showed
NET:recv, HDD:wait-filled, IP intact, output hook active and a scheduling monitor.
MiB counters rounded to zero cannot distinguish 0 bytes from a partial first bank.
This differs from DIAG-1/2's later packet loss; the original cause is unresolved.

The preceding screen omitted the real upload worker. DIAG-5 records the actual
calling thread ID in the transfer path and displays IO thread status instead of
the output producer thread status (output counters remain). The IO row records
getfl/setfl, deadline check, send/recv, returned result, delay, progress callback,
and fd restoration. It shows call/yield counts, last socket result/errno and
exact bytes moved within the current bank. A stage names a boundary, not proof
of the lower-level cause: e.g. recv can wait in an internal semaphore.
The first qualifying stall snapshot retains the IO trace and IO thread status.
The BD line moves to the last row; all 20 rows remain within the 80x40 screen.
The data loop adds only bounded diagnostic field writes, with no new syscalls,
sleeps, retry/cancel actions or RPC inside that loop. The transfer caller records
its ID once per data-I/O call using the EE-local GetThreadId syscall. Native tests check those writes
at actual delay/progress boundaries as well as all prior transfer contracts.
On real hardware, capture the IO trace plus IO wait/status/ID at the next stall
before choosing a fix. This revision does not claim the underlying stall or its
ineffective timeout has been repaired. Startup DHCP investigation is deferred
at the user's request.

## DIAG-6: restore required runtime initialization and remove RPC link polling

The application previously replaced _libcglue_init with an empty function.
The canonical SDK crt0 calls that function before main; its normal initializer
creates newlib locks and the descriptor manager. The application uses the EE
network library, malloc and POSIX sockets across multiple threads, so skipping
those gates is invalid. DIAG-6 calls __locks_init then __fdman_init, preserving
the deliberate exclusion of RTC, pthread and other full runtime startup work.
These are EE-local initialization steps and require no disk readiness or IOP
RPC. They run before the application's IOP reset, which leaves EE gates intact.

A native regression extracted the actual application initializer, SDK lock and
descriptor initialization, and application lock wrappers. Mock kernel gates
reproduced a network allocation using uninitialized gate0 (an unrelated busy
I/O gate), and descriptor gate -1. After the fix, the heap/descriptor gates are
valid and distinct; recursion and standard descriptor reservation also pass.
This proves a startup defect and its correction, not the exact branch of the
reported hardware hang. The test does not run the PS2 BIOS or linked SDK image.

Link polling now reads canonical NETMAN's EE event state instead of making a
synchronous NetManIoctl RPC on every poll. The canonical registration code
replays the link state after stack/RPC registration. A blocked RPC cannot be
bounded by the caller's loop count; removing it avoids that failure condition.
The redundant second link wait and unbounded mode-retry loop are removed.
The temporary detailed boot-stage messages used during investigation have
been removed at the user's request. Startup keeps its original single
Waiting for link + DHCP status and actionable warning/error messages.
No hardware spin-up delay, forced reset, socket behavior or disk settings change.

Verify repeated cold/warm boots with the newly built ELF, and no-carrier/no-DHCP
failure paths. A remaining stall at a named SDK initialization call still needs
its own diagnosis; the external polling limits cannot bound arbitrary internal
SDK waits or a failed timer. Original long-upload diagnosis resumes only after
startup is reliably usable. Actions also runs the native startup-poll test
before the ELF build. Do not claim the 90% hardware hang fixed until
repeated real boot checks pass.

## DIAG-7: startup polling must not sleep on the failed wake mechanism

The user reproduced DIAG-6 stuck at waiting for DHCP lease for more than five
minutes. While that state was reported, the Mac pinged 192.168.5.3 three times
successfully and verified the known PS2 MAC 00:04:1f:5d:c9:62. An address was
active; the screen did not alone prove that DHCP was still acquiring a lease.
There was no remote reboot or network/file mutation during this observation.

The actual sleepMs first called wrapped DelayThread and returned if it
succeeded. That wrapper always returned zero after its semaphore wait, so the
cooperative fallback below it could never run on the normal path. If the alarm
failed to release that wait, the outer 30-iteration limit could not be reached.
A native fault-injection test of the actual old sleepMs with a never-returning
alarm-dependent delay reproduced this indefinite wait; the child was stopped
by the test runner's timeout. This identifies a matching mechanism, not a PS2
thread-stack capture or proof of exactly why the hardware alarm failed.

Startup sleepMs now avoids DelayThread and semaphore waits altogether. It
lowers the caller below the network worker priorities, polls the running time
counter and rotates its own ready queue, then restores the original priority.
Status/priority/yield failures return errors rather than spinning at a priority
that could starve DHCP. This bounded busy wait is confined to startup; it uses
CPU while waiting and still requires the counter and scheduler to work. It is
not a hard timeout for a globally frozen timer/CPU or a blocking status getter.
Temporary per-stage and five-iteration progress output has been removed at
the user's request; the startup wait implementation and error reporting remain.
Missing carrier and missing lease retain separate 30-iteration limits.

Tests cover lost alarm wake, zero delay, existing lower priority, scheduler
errors and priority restoration. The actual link/DHCP polling functions also
check propagation of delay failure. Actions runs these native checks before
compilation. Transport timer/wait code, disk sequence and data I/O are unchanged.
Build and repeated real boot checks remain necessary before claiming the
reported permanent-wait symptom is fixed. DIAG-6 runtime-initialization fixes
remain; its hardware boot hang was not resolved by those fixes alone.

### Retained startup failure (after DIAG-7)

An immediate OPL fallback is not evidence of a normal DHCP timeout. The final
screen now retains one `DHCP fallback: <operation> (<return code>)` line.
The first error is preserved so an inner scheduler failure is not overwritten
by an outer link/DHCP timeout label. No healthy-stage/progress logs are restored.
This is observation only: scheduling and DHCP behavior are unchanged. Native
tests check first-error retention; the real PS2 failure remains to be captured.

### Launcher priority zero

The real PS2 fallback reported `thread priority (0)`: the DIAG-7 guard rejected
the startup thread before its first DHCP polling delay. Main now explicitly
sets priority 0x40 before network startup, following the owner's OPL pattern of
setting the main thread priority (OPL uses 31). Startup polling temporarily
lowers it to 0x60 and restores 0x40. It no longer depends on the launcher's
initial priority. The native harness executes the actual main setup followed
by the actual delay with initial priority 0: red before this change, green
after. This reproduces the reported guard failure, not BIOS execution; real
DHCP lease acquisition and the older long-transfer failure remain unverified.

### DIAG-7 transfer reproduction: timeout-slot ownership

2026-11-16 run `diag7-loop-20261116-140532`, source `9adaef6`:
- PS2 ACKed 413,015 payload bytes, then kept advertising a zero receive window.
- Client timed out after 120 seconds (524,288 bytes accepted locally); no retry
  or file deletion occurred. PS2 and router ping were 3/3; raw ARP was 4/4.
- Screen: IO delay, calls 81, yields 49, last recv -1/errno 11, current-bank
  bytes 347,490. Actual IO tid 16 waited on semaphore 29; HB and TCP stayed live.

The actual timeout-slot functions reproduce a lost wakeup with this schedule:
ISR releases waiter A and makes its slot free; B acquires that slot before A
runs; A's cleanup erases B's deadline. B can then remain in WaitSema forever.
`test-wait-slots.py` fails on the old code with this schedule. The ISR now marks
a released slot as still owned (state 2); only its original waiter frees it.
The native replay passes after this change and is included in the existing CI
workflow. This proves an application race and repairs it; the photo cannot
prove the exact slot transitions on PS2. Hardware retest is still required,
and the older multi-GiB network-silence failure is not declared solved.

### DIAG-8: TV-safe display, no network behavior changes

The user confirmed that the DIAG-7 screen loses the first characters on the
left and ends at SNAP IO: the CORE/RX snapshot and RING/BD rows were off-screen.
DIAG-8 uses columns 4..73, header rows 2..5, and all 20 diagnostic rows 6..25.
The native monitor render test rejects any line outside this rectangle, and
checks that RING/BD and the frozen IO/CORE/RX records remain visible together.
It fails on the old layout; live and post-cleanup snapshot cases pass on the
new layout. Actual TV margins still need user verification. The test does not
claim PS2 video emulation. Scheduling, timers, protocol and IOP/SDK unchanged.

The 25ac150 retest photo confirms the receive loop retried 46,057 times before
ETIMEDOUT (116), with the HDD waiting for a filled bank in the stall snapshot.
The previous permanently blocked DelayThread signature did not recur. Snapshot
IO was delay/errno11 with 425,280 bytes in the partial bank. Live OUT calls and
returns matched (2,166,520), queue 0, error 0; RX was asleep and message use 0,
not pool exhaustion. These EE observations do not yet distinguish empty EE RX
ring / stopped upstream delivery from pending descriptors with a missed wake.
The missing RING/BD information must be captured before selecting a new fix.
