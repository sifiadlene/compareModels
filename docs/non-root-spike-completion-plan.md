---
title: Non-root spike completion plan
description: Research and plan for fail-closed WSL verification of the non-root agent/executor spike (issue #23, PR #26).
---

# Non-root spike completion plan

Scope: finish [#23](https://github.com/sifiadlene/compareModels/issues/23) /
PR #26 against `Microsoft.WSL.Containers` **3.0.5**. This document records research and
implementation status for the spike.
Sources are first-party only. WSL source is the tag
[`3.0.5`](https://github.com/microsoft/WSL/tree/3.0.5) (commit `e7ef9a88db1e5e5745ed93bdce3eb9d8ee5f8aee`,
[tag ref](https://api.github.com/repos/microsoft/WSL/git/ref/tags/3.0.5)); semantics of the
Docker daemon come from Moby [`v28.0.0`](https://github.com/moby/moby/tree/v28.0.0).
Caveat: the WSL tag is not proven to be the exact source for the installed native DLL. The
`win-x64` DLL in the installed NuGet package and test output has the same SHA-256
(`B17D70B14C0ADE45B1430F01FAD8433269F0536F7DEE90C019B5F2DAF3BC46C2`); its local
disassembly matches the source flag mask. The Docker/Moby version and kernel inside the WSL
VM were not found in the tag source, so runtime-dependent behavior must be measured.

## Conclusions

1. **`Privileged = false` is a no-op in 3.0.5, and nothing in the API reports it.**
   The managed setter only toggles a flag bit
   ([ContainerSettings.cpp:159-172](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/winrt/ContainerSettings.cpp#L159-L172));
   the SDK masks container flags to auto-remove and GPU, with
   `// TODO: WSLC_CONTAINER_FLAG_PRIVILEGED has no associated runtime value`
   ([wslcsdk.cpp:51-59](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/wslcsdk.cpp#L51-L59),
   applied at [87-90](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/wslcsdk.cpp#L87-L90)
   and [1076](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/wslcsdk.cpp#L1076)).
   The Docker create request type has no `Privileged`, `SecurityOpt`, or `User`-for-exec
   field ([docker_schema.h:398-423](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/inc/docker_schema.h#L398-L423)),
   and inspect output has only `CapAdd`/`CapDrop` in `HostConfig`
   ([wslc_schema.h:97-100](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/inc/wslc_schema.h#L97-L100),
   mapped at [WSLCContainer.cpp:2568-2569](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/wslcsession/WSLCContainer.cpp#L2568-L2569)).
   So the property cannot have *caused* any effective state, and an absent inspect field
   is "unreported", never "false". This matches the existing Windows run
   (`docker-evidence.txt:44-50`).
2. **The SDK's public API cannot report the effective privileged flag.** The session's own
   Docker daemon can report it directly through a debug-only WSL command:
   `wslc --session <session-name> system session run docker inspect ...`. In a repeat run,
   this read-only check reported `HostConfig.Privileged=false` for all seven containers, and
   each inspected ID matched the Windows test evidence. This proves the daemon's effective
   record, **not** that the SDK property caused it. WSL's service IDL describes session
   process execution as meant for debugging
   ([wslc.idl:788](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/service/inc/wslc.idl#L788));
   it is not a supported public inspection API. Treat it as version-pinned, maintainer-only
   verification, and fail closed if the command is missing, times out, returns malformed
   output, identifies another container, or reports anything other than `false`.
3. **Kernel evidence independently detects privileged-like capability state.** In Moby,
   privileged mode returns *all* capabilities regardless of `CapDrop`
   ([oci/caps/utils.go:83-87](https://github.com/moby/moby/blob/v28.0.0/oci/caps/utils.go#L83-L87)),
   selects the unconfined AppArmor profile
   ([daemon/oci_linux.go:131-132](https://github.com/moby/moby/blob/v28.0.0/daemon/oci_linux.go#L131-L132)),
   skips the default seccomp profile
   ([daemon/seccomp_linux.go:24-30](https://github.com/moby/moby/blob/v28.0.0/daemon/seccomp_linux.go#L24-L30)),
   and makes cgroups writable
   ([oci_linux.go:667-668](https://github.com/moby/moby/blob/v28.0.0/daemon/oci_linux.go#L667-L668)).
   Docker documents the same ("gives all capabilities", all devices, relaxed AppArmor/SELinux;
   [docker run docs](https://docs.docker.com/engine/containers/run/), `--privileged`).
   Therefore an exact `CapBnd` equal to the requested set (existing test expects `0x1c0` =
   SETUID|SETGID|SETPCAP) is a daemon-derived fact that a Moby-based daemon could not
   produce under privileged mode. It is **evidence about the daemon's effective state, not
   about the SDK property**, and is only as version-bound as the daemon (hence the version
   gate below). `CapBnd`, `CapEff`, `NoNewPrivs`, and `Seccomp` are standard `/proc/<pid>/status`
   fields ([proc_pid_status(5)](https://man7.org/linux/man-pages/man5/proc_pid_status.5.html)).
4. **Seccomp is a secondary indicator only.** Moby silently runs *without* the default
   profile if the kernel lacks seccomp
   ([seccomp_linux.go:31-37](https://github.com/moby/moby/blob/v28.0.0/daemon/seccomp_linux.go#L31-L37)),
   and the WSL VM kernel's config was not found. Record `Seccomp:`; do not gate on `2` until a
   maintainer run shows it is available.
5. **The wrapper, not the runtime, supplies `no_new_privs`.** The SDK discards user control
   for init and exec (`// TODO: No user access`,
   [wslcsdk.cpp:333-336](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/wslcsdk.cpp#L333-L336),
   [1183-1191](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/wslcsdk.cpp#L1183-L1191)),
   although the service layer has a `User` path
   ([WSLCContainer.cpp:2827-2830](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/wslcsession/WSLCContainer.cpp#L2827-L2830)).
   `setpriv --no-new-privs` blocks setuid/file-capability gains across `execve` and cannot be
   unset ([kernel no_new_privs](https://docs.kernel.org/userspace-api/no_new_privs.html),
   [setpriv(1)](https://man7.org/linux/man-pages/man1/setpriv.1.html)); it does not stop
   privilege changes that avoid `execve`, so it complements, not replaces, the empty bounding set.
6. **Capability additions can be policy-blocked.** `CapAdd.Count > 0` fails with
   `WSLC_E_CAPABILITY_ADDITIONS_DISABLED` when `AllowWSLContainerPrivileged` is disabled
   ([WSLCSession.cpp:2423-2428](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/wslcsession/WSLCSession.cpp#L2423-L2428);
   needs the newer session interface, [wslcsdk.cpp:1060-1064](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/wslcsdk.cpp#L1060-L1064)).
   `SETUID`, `SETGID`, `SETPCAP`, `CHOWN`, `FOWNER`, `DAC_OVERRIDE` are all in Moby's *default*
   set ([oci/caps/defaults.go](https://github.com/moby/moby/blob/v28.0.0/oci/caps/defaults.go)),
   so a drops-only configuration (default set minus everything not needed) needs no additions;
   `ALL` drop + adds is the stricter spelling ([utils.go:112-114](https://github.com/moby/moby/blob/v28.0.0/oci/caps/utils.go#L112-L114)).
   Either is acceptable if the same `CapBnd` gate passes; a policy refusal is a recorded
   blocker, never a reason to use privileged mode.
7. **`ContainerVolume` ownership is whatever the share driver presents.** Binds are created
   as `/mnt/<guid>` shares ([WSLCContainer.cpp:2906-2911](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/wslcsession/WSLCContainer.cpp#L2906-L2911),
   [PrepareBindMount 797-834](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/wslcsession/WSLCContainer.cpp#L797-L834))
   and mounted as 9p or virtiofs with only `ro`/`rw` options - no uid/gid/mode
   ([WSLCVirtualMachine.cpp:1248-1269](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/wslcsession/WSLCVirtualMachine.cpp#L1248-L1269));
   the C# type has no owner field
   ([containervolume.md](https://github.com/microsoft/WSL/blob/3.0.5/doc/docs/api-reference/csharp/data-classes/containervolume.md)).
   The recorded `0:0 0777` raw mount means a raw mount *already* permits any uid; that outcome is
   not evidence of correct ownership. The only first-party ownership control is for
   **named VHD volumes**: `Session.CreateVhdVolume` with `VhdOptions.Owner` sets uid/gid on the
   volume root at mkfs ([wslcsdk.idl:88,314-337](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/winrt/wslcsdk.idl#L314-L337),
   [wslcsdk.cpp:794-800](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/WslcSDK/wslcsdk.cpp#L794-L800)).
   It is not host-visible, so it only suits non-host workspaces.

## Initial gaps in the PR branch (head `0db35ca`)

| Gap | Where | Why it breaks acceptance |
| --- | --- | --- |
| `Privileged` was absent from SDK inspect, causing the existing test to fail | `NonRootSpikeTests.cs` | Prevented a completed Windows run (AC2); now verified with exact same-session daemon inspection. |
| Init process state and workspace filesystem type were not recorded | `NonRootSpikeTests.cs` | Environment evidence was incomplete; now recorded per container. |
| No uid-1001 workspace denial control | `NonRootSpikeTests.cs` | Ownership proof lacked a negative access check; now tested after provisioning. |
| README and evidence lacked Windows verification | README/evidence | README and evidence now reflect the successful Windows run. |

## Verification approaches

| Approach | Verdict | Reason |
| --- | --- | --- |
| `Privileged=false` in settings / inspect `Privileged` | Not viable | No-op and unreported (conclusion 1). |
| Same session's daemon: `wslc --session <name> system session run docker inspect ...` | **Direct, observed, debug-only** | Confirms the effective daemon record for the exact container; tested against this session for all seven containers. Not a supported SDK API and does not prove the setting caused the value. |
| Inspect `CapAdd`/`CapDrop`/`Cmd`/`Entrypoint`/`Image` | Viable, configuration only | Proves the request reached the service, not kernel state. |
| In-container init-context `/proc/1/status` + `/proc/self/mountinfo` + `/proc/mounts` | **Independent cross-check** | Reads kernel-visible capability and mount state; fail on missing or malformed output. |
| Wrapped-process state (`check-state`) | **Primary** | Existing; exact zero `CapEff/Bnd/Inh/Amb`, `NoNewPrivs=1`, uid/gid 1000. |
| Seccomp, read-only `/proc/sys` and `/sys` | Secondary, record | Kernel/daemon dependent (conclusion 3). |
| Enable `Privileged=true` as a negative control | Reject | No effect today, and would run privileged if a later runtime honors it. |
| Docker/Podman on the runner | Evidence for wrapper mechanism only | Does not validate WSL (separate daemon, share driver). |

## Implementation and test sequence

1. **Pin and gate the environment**: require `Microsoft.WSL.Containers` service 3.0.5 and
   valid image-reference/configuration-digest inputs. Record the service version, kernel
   version, and container mount type. A different service version requires re-verification
   of source and semantics. *(AC2.)*
2. **Fail closed on effective privileged state**: while each uniquely named test container
   is running, use the session's display name with the same-session
   `wslc ... system session run docker inspect` debug route. Query the full container ID
   returned by SDK inspection and validate the response's ID, name, and Boolean
   `HostConfig.Privileged` value. Require `false`; missing CLI/session,
   timeout, nonzero exit, malformed output, identity mismatch, missing value, or `true` fails the
   test. Keep this read-only host-side check out of workload containers. Also record
   init-context `/proc/1/status` and mount information as independent evidence. Label this
   as observed effective state, not proof the SDK property was honored. If policy disallows
   debug-only WSL commands, record the criterion as blocked until a supported API exists.
   *(AC2.)*
3. **Preserve the existing privilege controls**: keep the current root, retained-capability,
   effective-capability, setuid/su/sudo, and per-capability-omission checks. The current
   `CapBnd=0x1c0` assertion checks the container's configured bootstrap set; retain it as
   additional evidence. Add focused parser tests only if new parsing logic is introduced.
   *(AC1, AC2.)*
4. **Keep the capability policy explicit**: retain the existing `ALL` drop plus additions.
   A policy refusal is a recorded blocker, never a reason to use privileged mode. Do not
   silently fall back to another capability set. *(AC1.)*
5. **Complete the ownership check** on the real `ContainerVolume`: record fstype, raw and provisioned
   `uid:gid:mode`, `chown` result, remount persistence, host-side re-read, 0660 file creation by both roles
   with `umask 007`; add a **uid 1001 denial** check after provisioning to demonstrate that
   the mode is not world-writable. If any step fails, record a blocker; do not fall back to
   `chmod 777`. *(AC4.)*
6. **Verify the configurable image recipe**: retain the per-Task `TASK_PACKAGES` build
   argument and record the declared packages and image digests; keep escalation fixtures
   explicitly test-only. *(AC3.)*
7. **Repeat and retain maintainer evidence**: Docker verification and the Windows test with
   the direct daemon check pass. Sanitized evidence and README record the results without
   registry auth. *(AC1, AC2.)*

## Acceptance mapping

| Criterion | Evidence needed | Status |
| --- | --- | --- |
| Positive/negative controls on Docker with evidence | `verify-docker`, `docker-evidence.txt` | Present. |
| Windows test fails closed on any unverified control | Steps 1-3 | Passed locally: exact daemon ID/name and `Privileged=false` required for every container. |
| Per-Task configurable tools, digest recorded | Step 6, build-arg `TASK_PACKAGES` | Implemented; package declaration and image digests recorded. |
| Ownership documented; blockers recorded | Step 5 | Passed on this host; `fuseblk`, ownership persistence, UID-1001 denial, and shared files recorded. |

## Blockers and open decisions

- No supported SDK API reports `Privileged` in 3.0.5. The same-session daemon check is
  direct but debug-only; if it is unacceptable for test automation, record the criterion as
  blocked until WSL exposes a supported check or the acceptance criteria are revised.
- `AllowWSLContainerPrivileged = Disabled` blocks capability additions on a host.
