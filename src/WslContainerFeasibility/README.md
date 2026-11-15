---
title: WSL container feasibility
description: Run the controlled Windows fixture and integration checks for native WSL containers.
---

## Purpose

This Windows-only feasibility slice exercises the native
`Microsoft.WSL.Containers` API with a pinned Alpine image, without Docker
Desktop. The WinUI interface checks prerequisites, runs a fixed non-sensitive
fixture, supports cancellation, and displays its outcome and evidence path.

Before the fixture starts, the runner inspects its no-mount, no-network
container and tests synthetic host-file, credential-file, environment, and
network canaries. A separate bridged control uses a read-only mount of the
synthetic files and connects to a local endpoint. A failed or unverified check
blocks execution. The fixture itself has no host mounts or network.

The runner does not use real credentials, install WSL components, or make
elevated system changes. It creates a unique session, pulls the pinned public
image, and cleans up its own containers and session. A registry connection is
needed for the image pull. The runner saves outcomes under
`%LOCALAPPDATA%\compareModels\Feasibility\Evidence` as JSON with the Windows,
WSL, container, check, and cleanup details, and reports a persistence failure
if it cannot save them. Closing the window during a run requests cancellation
and waits for cleanup. There is no unsandboxed fallback.

These checks do not prove general host-file isolation, protection of real
credentials, or resistance to deliberate container escapes. They do not cover
arbitrary coding Tasks, Copilot SDK authentication, MCP, or crash and restart
cleanup. The independently created control container is not a pre-existing
user container.

## Run the interface

Use an x64 Windows machine with .NET 10 and WSL Containers `3.0.5` or later.
The app reports missing WSL prerequisites without installing them. Build from
a Windows-local copy of the repository: the Windows manifest tool used by the
self-contained Windows App SDK build failed on the `\\wsl.localhost` path
tested here, and WinUI activation failed when launched from that share.

```powershell
dotnet build src/WslContainerFeasibility.WinUI/WslContainerFeasibility.WinUI.csproj
.\src\WslContainerFeasibility.WinUI\bin\Debug\net10.0-windows10.0.26100.0\win-x64\WslContainerFeasibility.WinUI.exe
```

Select **Check prerequisites**, then **Run fixture**. The run repeats the
prerequisite check even if it was not selected separately.

## Run Windows integration tests

From PowerShell on Windows with WSL Containers available:

```powershell
Push-Location src/WslContainerFeasibility.Tests
dotnet test
Pop-Location
```

The tests exercise the real native API, negative and unverified boundary
checks, cancellation and timeout, and preservation of a control container in
another session. Descendant-stop tests mount only a synthetic temporary
heartbeat directory and use the same process-stop path as the fixture runner.
These tests pull the pinned image and save local evidence.

## Non-root agent/executor spike (#23)

The isolated recipe and Linux probes are in `src/NonRootSpike`; the existing
Alpine feasibility fixture is unchanged. The two roles here are synthetic
workloads, not an integration with an agent SDK. Runner observations and the
resulting OCI manifest/configuration digests are in
[`docker-evidence.txt`](../NonRootSpike/docker-evidence.txt).

**Test-only image. Do not deploy it for real Tasks.** It contains a root-owned
setuid probe, a passwordless `sudo id` rule, and permissive `su` PAM rules so the
unsafe controls really can regain root. The root account remains locked and
there are no credentials. These intentional escalation fixtures must not be
copied into a production workload image. Run only the supplied synthetic
checks; the wrapper is not a boundary against a host-side caller that can
choose an arbitrary root entry point.

### Build tools per Task, outside the WSL API

From the repository root on a Linux Docker builder:

```bash
docker build --build-arg TASK_PACKAGES='git jq' \
  -t comparemodels-nonroot-spike "$PWD/src/NonRootSpike"
bash "$PWD/src/NonRootSpike/verify-docker" comparemodels-nonroot-spike
docker image inspect comparemodels-nonroot-spike --format '{{.Id}}'
docker save --output /tmp/comparemodels-nonroot-spike.tar comparemodels-nonroot-spike
```

`TASK_PACKAGES` is the Task's declared, space-separated Debian package list,
not a hard-coded coding tool set. An empty list is supported; versions can
be declared with `package=version`. Only the privilege-test infrastructure
(`util-linux`, `passwd`, `sudo`, and a separate build-stage compiler) is fixed.
All installation happens at build time. `/etc/task-packages` records the
declaration, and `/etc/image-packages` records the full installed inventory.
Build a separate image for each Task declaration; do not install tools when
starting a Run. The base is digest-pinned; Debian package repositories can
change, so a rebuild need not have the same digest. Retain the built image,
not just its recipe, for reproducibility.

The evidence records the local **configuration digest** (`docker inspect .Id`),
the saved archive's **OCI manifest digest**, and the archive transport checksum.
These are not fabricated pullable registry references: pushing may recompress
layers and change the manifest digest. To run on Windows, a maintainer
must first publish that image to an accessible registry using their normal,
approved publication workflow, record the returned **repository manifest
digest**, and pull `registry/repository@sha256:...`. For example, tag the
already-built image for the registry, push it, and obtain its `RepoDigests`
with `docker image inspect`. Record both that immutable reference and `.Id`.
Do not pass the configuration digest as a repository manifest digest.
No image has been published as part of this spike. Build for the Windows WSL
machine's Linux architecture (the runner evidence is `linux/amd64`).

`Microsoft.WSL.Containers` cannot build images. This test uses
`Session.PullImageAsync` on the externally built/published image. For offline
transport, the SDK also exposes image loading/importing, but that path is not
exercised here; do not substitute a rootfs import without re-verifying image
metadata and setuid modes.

### Minimal privileges and controls

All workload containers are non-privileged and have no network. The WSL test
sets `Privileged = false`, `CapabilityDrops = ["ALL"]`, and
`CapabilityAdditions = ["SETUID", "SETGID", "SETPCAP"]`. Docker uses the
equivalent `--cap-drop ALL` plus those three additions. They are only the
trusted wrapper's bootstrap capabilities:

| Retained before the wrapper | Why necessary | Omission observed on Docker |
| --- | --- | --- |
| `SETUID` | Change real/effective/saved UID to 1000 | `setresuid` fails (127) |
| `SETGID` | Change GID and clear supplementary groups | `setresgid` fails (127) |
| `SETPCAP` | Empty the capability bounding set | `setpriv` can succeed but leaves `CapBnd=...00c0`; verifier fails (1) |

`drop-workload` clears inheritable, ambient and bounding sets, sets
`no_new_privs`, clears supplementary groups, and execs the supplied workload
as UID/GID 1000. The verifier requires zero effective, bounding, inheritable
and ambient capabilities and `NoNewPrivs=1`. Missing tools, failed controls,
unexpected exits, and missing success markers fail the check.

Three deliberately unsafe states must be rejected by that same verifier:
root; UID 1000 with a retained bounding set; and UID 1000 with effective
`SETUID` despite an empty bounding set. Unsafe baseline controls separately
prove that `su`, `sudo`, and the setuid probe can regain root. After the
wrapper, all three fail. Each bootstrap capability is also omitted in turn.
Both Docker and WSL rely on the in-container wrapper for `no_new_privs`,
not a runtime security flag or a nonexistent user field in `ProcessSettings`.

The WSL workload init process is also wrapped. **Every subsequent
`CreateProcess` call must include the wrapper**: the API starts a new process
and does not promise to inherit init's dropped UID or `no_new_privs`. Negative
controls intentionally use unwrapped processes in separate control
containers. Do not expose this trusted host-side API directly to a workload.
This proves process privilege dropping, not resistance to kernel escapes.

### Windows integration and mount-ownership blocker

Run on Windows with .NET 10 and `Microsoft.WSL.Containers` **3.0.5** (the exact
package version pinned by the existing project):

```powershell
$env:NONROOT_SPIKE_IMAGE = 'registry/repository@sha256:<published-manifest-digest>'
$env:NONROOT_SPIKE_IMAGE_ID = 'sha256:<configuration-digest-of-that-image>'
dotnet test src/WslContainerFeasibility.Tests/WslContainerFeasibility.Tests.csproj `
  --filter FullyQualifiedName~NonRootSpikeTests --logger 'console;verbosity=detailed'
```

Missing digest inputs fail rather than skip. Ordinary feasibility tests can
still be selected with `--filter FullyQualifiedName~WslFixtureRunnerTests`.
The new test uses unique containers/session storage, bounds commands/pulls,
checks the inspected image ID, non-privileged flag and capability lists,
and saves inspection, raw/provisioned mount ownership, command exits and
output under `%LOCALAPPDATA%\compareModels\Feasibility\Evidence\nonroot-*.txt`.
Only `WINDOWS_SPIKE_OK` **and** `CLEANUP_OK` indicate a completed test; retain
the test result too. There is no catch-and-pass fallback.

On Docker's Linux bind mount, ownership is numeric and shared across
containers: trusted provisioning sets the directory to `1000:1000`, mode
`0770`; both roles create `1000:1000` files with mode `0660` using umask `007`,
read and append to the other role's files, without `chmod 777`.
Provisioning/cleanup are separate trusted root operations, not workload
capabilities. They use only the needed ownership/access capabilities and
never privileged mode.

**WSL `ContainerVolume` ownership is unverified, not assumed to match Docker.**
The API's volume constructor has host path, container path and read-only
fields, not UID/GID mapping. The Windows test records the initial UID/GID/mode
of a synthetic Windows-local temporary directory, attempts `chown`/`chmod`
in a separate provisioning container, then remounts it in both workload
containers. The shared probes require ownership `1000:1000`, directory mode
`0770` and file mode `0660` after remounting. Windows must also read both
resulting files. This detects ownership or permission emulation that does
not persist across `ContainerVolume` mounts.

The cloud runner cannot exercise WSL, so the Windows ownership observation
and final WSL acceptance remain **blocked pending a maintainer's local run**.
If the mount ignores ownership/mode changes, or host policy disallows
capability additions, retain the failing transcript and investigate a
supported workspace provisioning/storage path separately. Do not relax
permissions, enable privileged mode, add capabilities to the workload, or
install a custom seccomp profile to force a pass.

## Run the original console harness

```powershell
dotnet run --project src/WslContainerFeasibility/WslContainerFeasibility.csproj
```

Add `-- --fixture` to run the same controlled fixture as the interface. Without
that argument, the original harness performs its separate feasibility checks
and leaves its pinned images in the named harness session-storage directories
under `%LOCALAPPDATA%\compareModels`.
