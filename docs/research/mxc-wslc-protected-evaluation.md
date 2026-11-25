# MXC WSLC protected Evaluation research

Issue: [#31](https://github.com/sifiadlene/compareModels/issues/31).
Research and synthetic Windows observations: 2026-11-21.

## Plan and decision

Keep this a separate console spike, not a replacement for native WSL or a
multi-backend framework. Verify the actual packaged contract and telemetry,
prepare a pinned image outside Evaluation, then require effective in-guest
and independent container-inspection gates before running the native
baseline's synthetic samples. Verify result distinctions, descendants,
retention, and scoped cleanup on Windows. An unsupported or unverified gate
blocks execution; no host-native or unsandboxed fallback is allowed.

Use `Microsoft.Mxc.Sdk` 1.1.0, release source
[`c66201839d63cae2239db5e92f1f1d6076cf5181`](https://github.com/microsoft/mxc/tree/c66201839d63cae2239db5e92f1f1d6076cf5181).
Do not derive this release's behavior from newer `main` documentation.

## Primary-source findings

| Area | Release finding and implementation consequence |
| --- | --- |
| Package | [NuGet 1.1.0](https://www.nuget.org/packages/Microsoft.Mxc.Sdk/1.1.0) supplies .NET 8/10 assemblies and Windows x64 native assets. The [package archive](https://api.nuget.org/v3-flatcontainer/microsoft.mxc.sdk/1.1.0/microsoft.mxc.sdk.1.1.0.nupkg) SHA256 is `3dc3d802bbf7eff1b2360725f4f63c5ba403725ceef1b62091a9e23a5293fb59`. |
| Contract | [`ContainerRequest`](https://github.com/microsoft/mxc/blob/c662018/sdk/dotnet/Microsoft.Mxc.Sdk/V1/ContainerRequest.cs) must explicitly use `Containment.Wslc`. The [one-shot writer](https://github.com/microsoft/mxc/blob/c662018/sdk/dotnet/Microsoft.Mxc.Sdk/V1/ExactOneShotRequestWriter.cs) emits stable contract `1.0.0`; the default containment is not WSLC. |
| Prerequisites | [WSLC getting started](https://github.com/microsoft/mxc/blob/c662018/docs/backends/wslc/wsl-container-getting-started.md) requires WSL 3.0.5 or newer and Virtual Machine Platform. This spike further pins its debug inspection to service 3.0.5. |
| Image acquisition | [`image.rs`](https://github.com/microsoft/mxc/blob/c662018/src/mxc-sdk/src/backends/wslc/common/image.rs#L647-L723) loads a Docker-save archive's tags, not a requested manifest digest. The runner later uses `Image` verbatim. Digest references do not hit the cache ([lines 923-935](https://github.com/microsoft/mxc/blob/c662018/src/mxc-sdk/src/backends/wslc/common/image.rs#L923-L935)); an uncached pull is refused with egress denied. Use a validated archive and matching local tag. |
| Filesystem | [Policy mapping](https://github.com/microsoft/mxc/blob/c662018/src/mxc-sdk/src/backends/wslc/common/policy_mapping.rs) maps granted local drive directories to `/mnt/<drive>/...` with read-only/read-write mounts. It documents junction/hard-link and TOCTOU limitations. Inspect actual mounts and exercise writes; a policy name or hash is insufficient. |
| Network | [Policy implementation](https://github.com/microsoft/mxc/blob/c662018/src/mxc-sdk/src/backends/wslc/common/policy.rs#L116-L137) maps denied egress to network `none`. Deny ingress and host loopback too; request no proxy or port mappings. |
| Privileges | [Container setup](https://github.com/microsoft/mxc/blob/c662018/src/mxc-sdk/src/backends/wslc/common/wsl_container_runner.rs#L1279-L1330) exposes no workload user or full capability-drop policy. The source permits a trusted root command to invoke `setpriv`; verify the effective non-root state before sample execution. No read-only root guarantee is available here. |
| Cancellation | [`MxcContainer.RunAsync`](https://github.com/microsoft/mxc/blob/c662018/sdk/dotnet/Microsoft.Mxc.Sdk/V1/MxcContainer.cs) wraps synchronous native work; cancelling awaiting is not process termination. Use `Spawn`, claim streams before `WaitAsync`, close stdin, and explicitly kill on the host deadline or cancellation. |
| Lifecycle | [One-shot runner](https://github.com/microsoft/mxc/blob/c662018/src/mxc-sdk/src/backends/wslc/common/wsl_container_runner.rs#L595-L618) uses the request's container ID as its session name and supports a separate storage path. Container names/IDs and effective inspection are not exposed by the typed .NET API. One-shot use avoids the separate lifecycle daemon. |
| Telemetry | [Release telemetry documentation](https://github.com/microsoft/mxc/blob/c662018/docs/telemetry.md) describes Required ETW operational diagnostics with no packaged runtime opt-out. Commands, full paths, credentials, and customer content are excluded. MXC itself does not upload; Windows pipeline routing is not established by this spike. Read consent/policy state without changing it. |

## Observed packaged Windows behavior

The packaged .NET integration loaded native MXC **1.1.0** and reported WSLC in
available platform methods on Windows `10.0.26300.9974`, WSL `3.0.5.0`,
service/package `3.0.5`, kernel `6.18.58.1-1`, .NET runtime `10.0.15`,
and SDK `10.0.404`. Required diagnostic execution was explicitly approved;
MXC consent remained `Undetermined` and optional policy `Unrestricted`.

The imported Debian image configuration was
`sha256:43f84a0b210f98845ef003912981774c95efe439eaa4b1cb62cf803fd46fc82a`,
from the native baseline's manifest
`sha256:734a72b3d6142a71993b4bc580ca01d8ca687f77a2c422d96a961d3376b179bc`.
The prepared Docker-save archive SHA256 was
`1bf28789741ede1396a686105e54c923e729835de979e545bb639f7707e72056`;
configuration and layer hashes were validated before import.

The initial correct sample passed with independent inspection confirming
`Privileged=false`, network `none`, and the requested mount destinations and
access modes. That inspection did not establish the mounts' host sources.
The in-guest probes observed UID/GID 65534,
zero capabilities, `NoNewPrivs=1`, denied check/source writes, and writable
workspace controls. The owned session disappeared and its store was removed.
Detailed per-control observations and commands are maintained in the
[spike guide](../../src/MxcWslcFeasibility/README.md).

All 12 MXC Windows controls subsequently passed, covering the two failing
samples, unsafe protection controls, outcome distinctions, termination,
retention, and an unrelated container. Four final targeted controls passed
after adding daemon PID observations. The final runs reported no new daemon,
absent owned sessions, removed stores, and retained projects/evidence.
These results predate the stricter source check. A later run exposed
opaque `/mnt/{GUID}` mount sources, so the mounted design blocked Evaluation
before the sample ran. Host-source identity remains unverified; the current
synthetic runner instead requires zero mounts as described below.

### WSLC 3.0.5 mount-source investigation

On 2026-11-25, a temporary app-owned synthetic session exposed Docker bind
sources `/mnt/{GUID}`. Its daemon's `/proc/self/mountinfo` showed each of
those paths as a `virtiofs drvfs` mount with a *different* share GUID as its
root. The guest had no `/run/wsl/virtiofs` mapping directory. A Windows
`wslc --session <session> inspect --type container <id>` reported the
requested Windows paths; the Docker metadata label also paired those paths
with the opaque VM paths. These reports were captured during the temporary
investigation; the runner does not collect host inspection on each run.

The pinned WSLC 3.0.5 source explains the correlation, but also its limit:
[`PrepareBindMount`](https://github.com/microsoft/WSL/blob/e7ef9a88db1e5e5745ed93bdce3eb9d8ee5f8aee/src/windows/wslcsession/WSLCContainer.cpp#L797-L832)
generates the VM-path GUID and stores it with the canonical Windows path.
That same record is [serialized into the Docker label](https://github.com/microsoft/WSL/blob/e7ef9a88db1e5e5745ed93bdce3eb9d8ee5f8aee/src/windows/wslcsession/WSLCContainer.cpp#L3109-L3124),
[passed to the container](https://github.com/microsoft/WSL/blob/e7ef9a88db1e5e5745ed93bdce3eb9d8ee5f8aee/src/windows/wslcsession/WSLCContainer.cpp#L3198-L3207),
[used for mounting on start](https://github.com/microsoft/WSL/blob/e7ef9a88db1e5e5745ed93bdce3eb9d8ee5f8aee/src/windows/wslcsession/WSLCContainer.cpp#L1291-L1305),
and [reported by host inspection](https://github.com/microsoft/WSL/blob/e7ef9a88db1e5e5745ed93bdce3eb9d8ee5f8aee/src/windows/wslcsession/WSLCContainer.cpp#L2605-L2628).
Thus host inspection corroborates the Windows path, destination, type and
mode in the Docker label, while the label's VM path can be compared with the
Docker bind source. Both Windows-path reports derive from the same record;
they do **not** independently establish which Windows directory backs the
actual share.

The [share creation code](https://github.com/microsoft/WSL/blob/e7ef9a88db1e5e5745ed93bdce3eb9d8ee5f8aee/src/windows/wslcsession/WSLCVirtualMachine.cpp#L1222-L1268)
obtains a separate share GUID and sends it to the guest without the Windows
path. Although ordinary WSL's drvfs code [can save a tag-to-source mapping](https://github.com/microsoft/WSL/blob/e7ef9a88db1e5e5745ed93bdce3eb9d8ee5f8aee/src/linux/init/drvfs.cpp#L765-L795),
that mapping was absent in this WSLC session. Guest inode numbers also did
not match the Windows file IDs in the synthetic probe. None of these
observations independently maps the mounted share back to the exact Windows
directory, so consistent reports cannot independently open the mounted gate.
The current evaluator avoids this unverified mapping by rejecting all mounts.

### Mountless alternative

A temporary synthetic MXC WSLC experiment omitted the working directory and
filesystem grants. Before release through
[MXC process stdin](https://github.com/microsoft/mxc/blob/c66201839d63cae2239db5e92f1f1d6076cf5181/sdk/dotnet/Microsoft.Mxc.Sdk/V1/MxcProcess.cs),
inspection reported no mounts, network `none`, and non-privileged mode; the
normal run completed and its session disappeared. In separate simulated
timeout and cancellation runs, a debug-only same-session `docker pause`
stopped a heartbeat while `docker cp` captured the workspace before `Kill`.
The captures survived cleanup. In a separate synthetic run, a verified pause
followed by a deliberately failed copy returned exit 1; `Kill` returned in
278 ms, the wait completed in 291 ms with exit 137, and the owned session was
absent before its store was removed. These are individual observations, not
proof that descendant processes are independently verified or that termination
is bounded under a stalled operation.

Docker documents that [`pause` freezes all container processes](https://docs.docker.com/reference/cli/docker/container/pause/)
and [`cp` can stream a tar archive](https://docs.docker.com/reference/cli/docker/container/cp/).
However, timing out and killing the host-side debug client does not establish
that a pause or copy already accepted by the daemon is cancelled. A late
pause could race the stop and cleanup. The managed [`Kill` is synchronous](https://github.com/microsoft/mxc/blob/c66201839d63cae2239db5e92f1f1d6076cf5181/sdk/dotnet/Microsoft.Mxc.Sdk/V1/MxcProcess.cs#L849-L877);
the MXC backend's [stopper calls `WslcStopContainer` with SIGKILL](https://github.com/microsoft/mxc/blob/c66201839d63cae2239db5e92f1f1d6076cf5181/src/mxc-sdk/src/backends/wslc/common/wsl_container_runner.rs#L1888-L1931),
and [WSLC may wait for a conflicting transition and Docker](https://github.com/microsoft/WSL/blob/e7ef9a88db1e5e5745ed93bdce3eb9d8ee5f8aee/src/windows/wslcsession/WSLCContainer.cpp#L1591-L1605).
Running `Kill` on another thread can initiate it independently but does not
prove it completes or that cleanup succeeds.

The current fixed-sample runner implements a mountless variant: it independently
inspects the sole container for the pinned image, zero mounts, `none` network,
and non-privileged mode before releasing its stdin gate. A root orchestrator
holds the check and expected output outside the writable workspace, runs the
subject without gate stdin and with dropped privileges, and stays alive after
the trusted result so the host can capture it. The Evaluation deadline starts
before gate release; an interrupted release starts Kill without waiting for
capture and is reported as an infrastructure failure with the prior
timeout/cancellation recorded. Pause and paused-state inspection share a
one-second deadline before the separately bounded copy; a `finally` path starts
Kill even if these operations fail or stall. Any capture or cleanup error overrides a success-shaped outcome. The
tar is size- and entry-limited, validated, and retained **without extraction**;
it remains untrusted and may contain symlinks. A stalled debug operation, Kill,
or disposal is reported as a cleanup failure, and the store and host canaries
are retained. Descendant-observation tasks are included in this ownership gate.

The final focused run passed 47 cases, including deterministic failed/stalled
pause, verification, and copy operations and synthetic correct, incorrect,
tampered, timeout, cancellation, and post-pause copy-failure controls. The
timeout/cancellation controls observed a descendant, a stopped heartbeat, and
an absent owned session. These are practical observations, not proof of a
mathematical termination bound or daemon-request cancellation; arbitrary
container escapes, crash recovery, and extraction safety are not established.

The native comparison initially exposed an image/probe mismatch: the Debian
network probe used `/bin/bash` in the Alpine bridged positive-control
container, then an Alpine-only probe was also used in the Debian no-network
evaluator. The positive probe now uses Alpine `busybox wget`; the Debian
no-network probe records the interface listing and attempts a Bash `/dev/tcp`
connection to the endpoint, alongside the independently inspected
`NetworkMode=none` setting and endpoint canary. The interface listing is
supplemental to the actual connection attempt. A Debian-specific test
confirmed that this same probe connects from a bridged container and fails
from an isolated container. The correct-sample test and the fixture's five
failed-boundary controls also passed on the recorded Windows environment.

## Comparison with native WSL

MXC supplies typed filesystem/network policy mapping, image import, one-shot
process management, and session teardown. Its `wslc` backend is **not an
independent isolation boundary**. This package still needs a trusted privilege
wrapper and unsupported same-session inspection for the protections the
native baseline verifies explicitly.

Both current sample runners use in-container workspaces and retain untrusted tar
archives, but native WSL captures from a stopped container while MXC pauses
before capture because its one-shot path can remove the container on exit.
The positive canary control still uses host mounts and their host-tree
limitations apply. Required telemetry cannot be opted out of, whereas the
baseline did not add this MXC diagnostic layer. Neither spike proves
arbitrary-code safety or resolves every parent feasibility requirement.
