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

### Windows localhost forwarding spike

Run the forwarding integration test alone on the same Windows prerequisites:

```powershell
dotnet test src/WslContainerFeasibility.Tests/WslContainerFeasibility.Tests.csproj --filter "FullyQualifiedName~GivenMappedContainers_WhenForwardingIsProbed"
```

Or run it without xUnit and print the evidence path:

```powershell
dotnet run --project src/WslContainerFeasibility/WslContainerFeasibility.csproj -- --port-forwarding
```

The spike uses a pinned Alpine HTTP listener on `0.0.0.0:18080` in a
bridged container and maps an automatically selected Windows port to
`127.0.0.1`. It verifies the response contains a per-run marker. It probes
each active non-loopback host address, first checking that a separate local
listener is reachable on that address; absent or inaccessible interfaces
make the result unverified, not a pass. It also requires both `Inspect()` and
the Windows TCP listener table to report a loopback-only binding. These local
checks do not establish remote reachability or firewall behavior; verify from
another machine separately if that threat model matters. A second bridged container verifies
the listener's container address before and after the same request from a
`None` container. Only the spike's uniquely named session and containers are
cleaned up. The test also checks that a container in another session survives.

The JSON evidence includes the complete `Inspect()` output, including network
and port information, for the mapped containers and controls. WSL Containers
does not support port mappings for containers with networking disabled, so
`noneMapping` records `Unsupported` when the API rejects that configuration.
The separate `None` control still verifies that a no-network container cannot
reach the bridged listener directly. Other API rejections, a missing listener,
an unexpected response, an unavailable host interface, or any other
unverified result blocks the spike and is recorded in the evidence; inspect
the error and retain the evidence as a forwarding blocker. There is no
unsandboxed fallback. The selected Windows ports are released before
container creation, so a port allocation race can also block the run.

The spike verifies the `Bridged` mapping behavior only; a `None` mapping cannot
be created through this API. `Bridged` is intended for inference connectivity,
not a claim that the container has no network. This synthetic HTTP listener
does not demonstrate a Copilot headless server or inference.

On Linux, only the platform-independent socket/HTTP probe tests can be run:

```sh
dotnet test src/WslContainerFeasibility.Tests/WslContainerFeasibility.Tests.csproj --filter FullyQualifiedName~WslPortForwardingProbeTests
```

## Run the original console harness

```powershell
dotnet run --project src/WslContainerFeasibility/WslContainerFeasibility.csproj
```

Add `-- --fixture` to run the same controlled fixture as the interface. Without
that argument, the original harness performs its separate feasibility checks
and leaves its pinned images in the named harness session-storage directories
under `%LOCALAPPDATA%\compareModels`.
