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
the pinned image before import; the archive hash is recorded per Evaluation.
The deterministic local tag is necessary because MXC's Docker-save import
does not map a registry manifest digest to the imported tag.

## Boundary and retained evidence

Each Evaluation has its own MXC session and image store. Egress, ingress, and
host loopback are explicitly denied. Only the generated workspace is mounted
read-write; a separate trusted-source directory is mounted read-only. A
separate positive control mounts synthetic host/credential canaries, while the
evaluator must be unable to read them.

A trusted root orchestrator provisions the acceptance check outside the
workspace and verifies its ownership, permissions, bytes, and ancestors. The
sample runs as UID/GID 65534 with all capability sets cleared and
`NoNewPrivs=1`. The orchestrator verifies writable-workspace controls,
direct/symlink/rename protection, source-mount write denial, and loopback-only
interfaces before announcing readiness. The Windows host independently
inspects the exact session's sole container, pinned image, non-privileged mode,
network `none`, and two mount grants before releasing the sample.

The trusted check's exit status is authoritative, not candidate output, a
workspace check, or a report. Post-check integrity is required for a pass or
executed failure. The Evaluation deadline is separate from image import and
boundary setup. Cancellation and timeout call `MxcProcess.Kill()`, not merely
cancel `RunAsync` awaiting. The descendant controls retain process observations,
a stopped heartbeat, and confirmation that the owned session disappeared.

JSON Run evidence is saved before final disposal and updated after cleanup
under `%LOCALAPPDATA%\compareModels\Feasibility\Evidence`. The resulting
workspace remains under `...\MxcWslc\<run-id>\workspace`, outside the deleted
image store. It is **untrusted and may contain Linux symlinks**: do not follow
links into sensitive locations. The spike retains this directory rather than
creating a Windows tar archive; Windows .NET archive creation did not handle
the observed Linux symlinks.

## Windows integration controls

Preparation must have completed. Running the integration tests is an explicit
diagnostics acceptance action:

```powershell
$env:MXC_SPIKE_ACCEPT_REQUIRED_TELEMETRY = '1'
dotnet test .\src\MxcWslcFeasibility.Tests\MxcWslcFeasibility.Tests.csproj
```

Without that setting, integration tests report the missing approval rather
than silently passing. The no-approval blocking test can run independently.

Controls cover correct/incorrect/tampered changes, writable checks, incomplete
privilege dropping, missing commands, subject exit 126, unavailable final
integrity, retention failure, timeout/cancellation descendants, retained
artifacts, and survival of an unrelated native WSL container.

### Observed Windows results

All 12 MXC controls passed on Windows `10.0.26300.9974`, WSL `3.0.5.0`,
WSLC `3.0.5`, kernel `6.18.58.1-1`, .NET runtime `10.0.15`, and SDK
`10.0.404`. A final focused rerun confirmed correct execution, no-approval
blocking, cancellation, and timeout after daemon-observation recording was
added. The console built without warnings.

| Control | Observed outcome |
| --- | --- |
| Correct / incorrect / tampered | Passed / Failed / Failed. Fake workspace checks, reports, and pass output did not change the authoritative failure. |
| Effective boundary | Network `none`, non-privileged mode, exactly two intended mounts, denied source/check writes, and verified non-root capability-free sample execution. |
| Unsafe / unavailable protections | Writable authoritative check, incomplete bounding-set drop, and missing final integrity were Blocked. |
| Result distinctions | Missing command was Unrunnable; subject exit 126 was an executed Failed check; a retention failure was InfrastructureFailure with the earlier Passed outcome preserved. |
| Cancellation / timeout | Distinct outcomes, a descendant observed before stopping, heartbeat stopped, and owned session absent afterward. |
| Persistence / cleanup | Project and JSON remained accessible; owned stores were removed and an unrelated container remained usable. No new lifecycle daemon was observed in the final runs. |

The native comparison initially exposed an image/probe mismatch: a Debian
network probe used `/bin/bash` in the Alpine bridged control, then an
Alpine-only probe was also used in the Debian no-network evaluator. The
positive probe now uses Alpine's `busybox wget`; the Debian no-network probe
records the evaluator's interfaces and attempts a Bash `/dev/tcp` connection to
the endpoint, alongside independent `NetworkMode=none` inspection and the
endpoint canary. The interface listing is supplemental to the actual connection
attempt. The focused native correct/incorrect/tampered and loopback tests passed
on the recorded Windows environment before this probe change; the revised probe
needs Windows revalidation. This fix is part of the current changes and not an
MXC boundary claim.

## Limitations

MXC WSLC uses the same underlying WSL boundary as the native baseline. These
fixed controls do not prove resistance to malicious container escapes or
readiness for arbitrary projects. MXC exposes no workload user/capability
settings here, so the trusted wrapper still performs and verifies the drop.
No read-only root filesystem is requested or claimed. Host-mounted trees have
documented junction/hard-link and canonicalization race limitations.

The one-shot path avoids the lifecycle daemon. Native startup/import cannot
be interrupted by cancelling a .NET await; cancellation is acted on once
`Spawn` returns a process handle. Abrupt termination, crash recovery, policy
enforcement against arbitrary malicious code, and telemetry routing remain
unverified. Any required parent protection not demonstrated remains blocked.
