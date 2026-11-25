# MXC WSLC protected Evaluation spike

This x64 Windows console experiment assesses issue [#31](https://github.com/sifiadlene/compareModels/issues/31)
with `Microsoft.Mxc.Sdk` **1.1.0**, explicitly selecting `Containment.Wslc`.
It does not adopt MXC for the application or silently fall back to another
backend. The native WSL spike remains unchanged.

## Prerequisites and telemetry

Use .NET 10, Windows with Virtual Machine Platform, and WSL Containers
**3.0.5**. The effective boundary inspection depends on the same version-pinned,
**debug-only** WSL container CLI used by the native baseline. There is no
supported MXC .NET inspect interface in this package.

The packaged MXC binary emits Required ETW diagnostic events and has no
supported opt-out. MXC documents operational IDs/outcomes, not commands, full
paths, credentials, or customer content. MXC does not upload these events
itself; Windows may route them through its diagnostic pipeline. This spike
does not establish the machine's routing behavior and does not change MXC
consent or Windows telemetry settings. Read the [primary-source research](../../docs/research/mxc-wslc-protected-evaluation.md)
before explicitly accepting diagnostics.

No live agent, token, user project, or inference is used. Only the supplied
synthetic samples are supported.

## Run

Prepare the image once. This separate host-side native WSLC session pulls the
digest-pinned Debian baseline from its registry and exports a Docker-save
archive. Registry connectivity is required for preparation, not Evaluation.

```powershell
dotnet run --project .\src\MxcWslcFeasibility\MxcWslcFeasibility.csproj -- prepare-image
dotnet run --project .\src\MxcWslcFeasibility\MxcWslcFeasibility.csproj -- correct --accept-required-telemetry
dotnet run --project .\src\MxcWslcFeasibility\MxcWslcFeasibility.csproj -- incorrect --accept-required-telemetry
dotnet run --project .\src\MxcWslcFeasibility\MxcWslcFeasibility.csproj -- tampered --accept-required-telemetry
```

`correct` should exit zero. `incorrect` and `tampered` should report an executed
failed acceptance check and exit nonzero. Missing prerequisites, unsupported
policies, unverified protections, and infrastructure failures are not passes.
During Evaluation, Ctrl+C requests cancellation and waits for owned-resource
cleanup. Abruptly terminating image preparation is not a verified cancellation
path.

The archive is under
`%LOCALAPPDATA%\compareModels\Feasibility\MxcWslc\image`.
Its configuration digest and every uncompressed layer are validated against
the pinned image before import, regardless of archive entry order; only the
manifest-selected configuration is trusted. The archive hash is recorded per Evaluation.
The deterministic local tag is necessary because MXC's Docker-save import
does not map a registry manifest digest to the imported tag.

## Boundary and retained evidence

Each Evaluation has its own MXC session and image store. Egress, ingress, and
host loopback are explicitly denied. The evaluator requests **no host mounts**.
A separate positive control mounts synthetic host/credential canaries, while
the evaluator must be unable to read them.

A trusted root orchestrator provisions the acceptance check outside the
workspace and verifies its ownership, permissions, bytes, and ancestors. The
sample runs as UID/GID 65534 with all capability sets cleared and
`NoNewPrivs=1`. The orchestrator verifies writable-workspace controls,
direct/symlink/rename protection and loopback-only
interfaces before announcing readiness. The Windows host independently
inspects the exact session's sole container, pinned image, non-privileged mode,
network `none`, and zero mounts before releasing the sample through stdin.
The sample receives no gate input.

The trusted check's exit status is authoritative, not candidate output, a
workspace check, or a report. Post-check integrity is required for a pass or
executed failure. The Evaluation deadline is separate from image import and
boundary setup and starts before the stdin release. If release stalls until
the deadline or cancellation, the host initiates `Kill` immediately; because
the project could not be captured safely, the result is an infrastructure
failure with the prior timeout/cancellation recorded. Otherwise, after
completion, cancellation or timeout, pause and paused-state verification share
a one-second deadline; only a verified pause permits the ten-second-bounded
copy. The host initiates `Kill` in a `finally` path if either step fails or
stalls. Capture or cleanup failure is reported as an infrastructure failure.
For the synthetic descendant control, the heartbeat child emits a run-specific
readiness marker after its first write. A subsequent process snapshot,
completed before stop starts, must show a distinct marked child whose PPID is
the sample process's PID. Readiness and observation never delay cancellation
or an independently initiated stop; missing, late, or invalid descendant
evidence is an infrastructure failure. The control also checks that the
heartbeat stopped and the owned session disappeared.

JSON Run evidence is saved before final disposal and updated after cleanup
under `%LOCALAPPDATA%\compareModels\Feasibility\Evidence`. `outcomeBeforeCapture`
is null until an actual pre-capture outcome is recorded; later capture or
cleanup failures can override the final outcome. The resulting
workspace is retained as `...\MxcWslc\<run-id>\workspace.tar`, outside the
deleted image store. The archive is limited to 1 MiB and 1,000 entries and is
validated for contained, unique entry paths and supported entry types. It is
**untrusted and may contain Linux symlinks**, including links outside the
workspace. The runner does not extract it; do not extract it into a trusted
directory or follow its links.

## Windows integration controls

Preparation must have completed. Running the integration tests is an explicit
diagnostics acceptance action:

```powershell
$env:MXC_SPIKE_ACCEPT_REQUIRED_TELEMETRY = '1'
dotnet test .\src\MxcWslcFeasibility.Tests\MxcWslcFeasibility.Tests.csproj
```

Without that setting, integration tests report the missing approval rather
than silently passing. The no-approval blocking test can run independently.
The deterministic shell regressions require Git for Windows `sh.exe` on
`PATH` and its `/usr/bin/timeout`.

Controls cover correct/incorrect/tampered changes, writable checks, incomplete
privilege dropping, missing commands, subject exit 126, unavailable final
integrity, retention failure, timeout/cancellation descendants, retained
artifacts, and survival of an unrelated native WSL container.

### Observed Windows results

The original mounted design was blocked before the acceptance check: WSLC
exposed opaque `/mnt/{GUID}` mount sources rather than the expected host paths.
A temporary investigation compared Windows-side WSLC
inspection with Docker's bind data and metadata; these reports share the same
source record and do not independently prove the Windows directory backing the
guest mount. See the [investigation](../../docs/research/mxc-wslc-protected-evaluation.md#wslc-305-mount-source-investigation)
for the version-pinned source and observed mapping limitation.

The mountless runner's 72 test cases passed on Windows
`10.0.26300.9974`, WSL `3.0.5.0`,
WSLC `3.0.5`, kernel `6.18.58.1-1`, .NET runtime `10.0.15`, and SDK
`10.0.404`, including deterministic stalled/failing-operation tests and real
synthetic Evaluation controls with the stricter marked-child observation.

| Control | Observed outcome |
| --- | --- |
| Correct / incorrect / tampered | Passed / Failed / Failed. Fake workspace checks, reports, and pass output did not change the authoritative failure. |
| Effective boundary | Network `none`, non-privileged mode, zero mounts, denied check writes, and non-root capability-free sample execution. |
| Unsafe / unavailable protections | Writable authoritative check, incomplete bounding-set drop, and missing final integrity were Blocked. |
| Result distinctions | Missing command was Unrunnable; subject exit 126 was an executed Failed check; a retention failure was InfrastructureFailure with the earlier Passed outcome preserved. |
| Cancellation / timeout | Distinct outcomes, a marked child and its sample parent observed by PID/PPID before stopping, heartbeat stopped, and owned session absent afterward. |
| Persistence / cleanup | A validated, unextracted project tar and JSON remained accessible; owned stores were removed and an unrelated container remained usable. A deliberately failed copy after pause produced an infrastructure failure. |

The native comparison initially exposed an image/probe mismatch: a Debian
network probe used `/bin/bash` in the Alpine bridged control, then an
Alpine-only probe was also used in the Debian no-network evaluator. The
positive probe now uses Alpine's `busybox wget`; the Debian no-network probe
records the evaluator's interfaces and attempts a Bash `/dev/tcp` connection to
the endpoint, alongside independent `NetworkMode=none` inspection and the
endpoint canary. The interface listing is supplemental to the actual connection
attempt. The revised Debian probe connected from a bridged container and was
blocked from an isolated container; the native correct-sample Evaluation also
passed. This is not an MXC boundary claim.

## Limitations

MXC WSLC uses the same underlying WSL boundary as the native baseline. These
fixed controls do not prove resistance to malicious container escapes or
readiness for arbitrary projects. MXC exposes no workload user/capability
settings here, so the trusted wrapper still performs and verifies the drop.
No read-only root filesystem is requested or claimed. The separate positive
canary control uses host mounts and has the documented host-tree limitations.

The one-shot path avoids the lifecycle daemon. Native startup/import cannot
be interrupted by cancelling a .NET await; cancellation is acted on once
`Spawn` returns a process handle. A timed-out debug client does not prove that
an in-flight daemon operation was cancelled. The SDK's synchronous `Kill` is
started independently, but completion is not guaranteed by a hard deadline;
if a debug operation (including descendant observation), termination or
disposal remains in progress, cleanup fails and the store and host canaries are
retained rather than deleted unsafely. Abrupt termination, crash recovery,
policy enforcement against arbitrary malicious code, and telemetry routing
remain unverified. Any required parent protection not demonstrated remains
blocked.
