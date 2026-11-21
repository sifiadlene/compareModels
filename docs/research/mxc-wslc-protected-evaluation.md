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
`Privileged=false`, network `none`, and exactly the requested read-only source
and read-write workspace mounts. The in-guest probes observed UID/GID 65534,
zero capabilities, `NoNewPrivs=1`, denied check/source writes, and writable
workspace controls. The owned session disappeared and its store was removed.
Detailed per-control observations and commands are maintained in the
[spike guide](../../src/MxcWslcFeasibility/README.md).

All 12 MXC Windows controls subsequently passed, covering the two failing
samples, unsafe protection controls, outcome distinctions, termination,
retention, and an unrelated container. Four final targeted controls passed
after adding daemon PID observations. The final runs reported no new daemon,
absent owned sessions, removed stores, and retained projects/evidence.

The native comparison initially exposed an image/probe mismatch: the Debian
network probe used `/bin/bash` in the Alpine bridged positive-control
container, then an Alpine-only probe was also used in the Debian no-network
evaluator. The positive probe now uses Alpine `busybox wget`; the Debian
no-network probe records the interface listing and attempts a Bash `/dev/tcp`
connection to the endpoint, alongside the independently inspected
`NetworkMode=none` setting and endpoint canary. The interface listing is
supplemental to the actual connection attempt. The focused native
correct/incorrect/tampered and loopback tests passed on the recorded Windows
environment before this probe change; the revised probe needs Windows
revalidation.

## Comparison with native WSL

MXC supplies typed filesystem/network policy mapping, image import, one-shot
process management, and session teardown. Its `wslc` backend is **not an
independent isolation boundary**. This package still needs a trusted privilege
wrapper and unsupported same-session inspection for the protections the
native baseline verifies explicitly.

The MXC sample workspace is an explicitly intended host mount and is retained
directly after cleanup; native WSL uses an in-container workspace and a
stopped-container archive. This difference introduces host-tree link/race
limitations and must not be generalized to user projects. Required telemetry
cannot be opted out of, whereas the baseline did not add this MXC diagnostic
layer. Neither spike proves arbitrary-code safety or resolves every parent
feasibility requirement.
