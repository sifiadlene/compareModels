---
title: WSL container API compatibility
description: Findings on WSL Containers 3.0.5 image commands and effective privileged-mode verification.
---

# WSL container API compatibility

## Version and source

The test pins `Microsoft.WSL.Containers` 3.0.5. The corresponding WSL source tag
is [`3.0.5`](https://github.com/microsoft/WSL/tree/3.0.5), commit
`e7ef9a88db1e5e5745ed93bdce3eb9d8ee5f8aee`. The installed `win-x64`
`wslcsdk.dll` is byte-identical between the NuGet package and the test output
(SHA-256
`B17D70B14C0ADE45B1430F01FAD8433269F0536F7DEE90C019B5F2DAF3BC46C2`).
Local disassembly matches the source flag mask, but the DLL has not been
independently mapped to that source commit.

## Findings

- **`Privileged` has no runtime effect in 3.0.5.** The WinRT setter toggles
  `WSLC_CONTAINER_FLAG_PRIVILEGED`
  ([ContainerSettings.cpp:159-172](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/winrt/ContainerSettings.cpp#L159-L172)).
  The SDK's `ConvertFlags` mask only retains auto-remove and GPU and marks
  privileged as having no runtime value
  ([wslcsdk.cpp:51-59, 86-91](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/wslcsdk.cpp#L51-L59)).
  The service's Docker `HostConfig` schema has no `Privileged` member
  ([docker_schema.h:398-424](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/inc/docker_schema.h#L398-L424)).
  Thus `Privileged = false` is not what makes the container non-privileged;
  the value is dropped before container creation.
- **SDK inspection cannot report that value.** The WSL inspection schema
  exposes `CapAdd` and `CapDrop`, but not `Privileged`
  ([wslc_schema.h:90-101](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/inc/wslc_schema.h#L90-L101)).
  A missing property is unreported, not `false`.
- **Effective daemon state has a direct, debug-only check.** While each
  container is alive, the Windows test calls the Docker daemon in that exact
  WSL session with
  `wslc --session <session-display-name> system session run docker inspect`.
  WSL's service IDL describes this process-execution interface as meant for
  debugging
  ([wslc.idl:788](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/service/inc/wslc.idl#L788));
  it is not a supported public inspection API. The test queries the exact ID
  returned by SDK inspection and requires the daemon's ID and name to match
  and `HostConfig.Privileged` to be `false`. Missing CLI/session, timeouts,
  failed commands, malformed output, identity mismatch, missing values, or
  `true` fail the test. This verifies the daemon's effective record, not that
  the SDK property caused it.
- **Kernel evidence checks workload confinement.** The test's Linux probes
  require UID/GID 1000, zero effective, bounding, inheritable, and ambient
  capabilities, and `NoNewPrivs=1`; the meanings of these `/proc` fields are
  documented in [`proc_pid_status(5)`](https://man7.org/linux/man-pages/man5/proc_pid_status.5.html).
  Init capability and seccomp state are recorded as supporting evidence, not
  as proof that the SDK setting was honored.
- **Image entrypoints are not overridden.** WSL sends
  `InitProcess.CommandLine` as the container `Cmd`, while the image entrypoint
  remains active
  ([WSLCContainer.cpp:2777-2785](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/wslcsession/WSLCContainer.cpp#L2777-L2785)).
  The test image therefore has no `ENTRYPOINT`; callers explicitly invoke the
  privilege-drop wrapper for workload processes.

## Verification result

On Windows 10.0.26300 with WSL Containers 3.0.5.0, the Windows integration test
passed with the same-session daemon check for all seven containers. Each
daemon-reported ID matched the corresponding SDK inspection ID, and every
daemon record reported `Privileged=false`. The SDK still reported no
`HostConfig.Privileged` field. The recorded false is the daemon's effective
default, not an effect of `ContainerSettings.Privileged`.

The same run recorded init-state capabilities and seccomp, confirmed the
workload wrapper's zero-capability/`no_new_privs` state, and observed the
`ContainerVolume` workspace as `fuseblk`. The raw directory mounted `0:0`
mode `0777`; trusted provisioning changed it to `1000:1000` mode `0770`,
UID 1001 was denied workspace access, and both UID-1000 roles shared files
with mode `0660`.

The same-session `wslc` check is suitable only as an explicit, version-pinned
maintainer test. Do not expose it to workloads or describe it as a supported
application API. If it is unavailable or changes, fail closed and treat
effective privileged-mode verification as blocked until WSL exposes a
supported check.
