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

## Prove one isolated Copilot run

The WinUI app also supports one fixed Copilot SDK attempt. Install the official
Copilot CLI and sign in with `copilot login` from **Copilot sign-in**. Then
select **Load strategies**, choose an available model, and select **Run Copilot
once**. The app asks for confirmation immediately before sending the request;
the attempt may consume billable Copilot usage. Cancelling or declining the
confirmation sends no request.

The runner gives Copilot only a custom shell tool that executes commands in a
new WSL container with no network or host mounts. It exposes a disposable
fixture, not this checkout. SDK built-in tools, skills, custom agents, MCP
servers, memory, and session storage are disabled. The host checks the fixture
with a protected acceptance command and captures the resulting source, patch,
SDK events, model identifiers when reported, usage-event presence, boundaries,
runtime versions, and cleanup outcome. Evidence is saved under
`%LOCALAPPDATA%\compareModels\Feasibility\Evidence`.

The CLI login state remains in the app's local Copilot directory on the host;
it is not copied into the task container. Strategy discovery and an actual
Copilot attempt require an authorized account and an available CLI/runtime.
Treat the proof as incomplete unless evidence records a completed attempt and
a passing protected acceptance check. Usage may not be reported by the SDK.
Synthetic canary checks do not prove general host isolation or resistance to
deliberate container escapes.

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

## Run the original console harness

```powershell
dotnet run --project src/WslContainerFeasibility/WslContainerFeasibility.csproj
```

Add `-- --fixture` to run the same controlled fixture as the interface. Without
that argument, the original harness performs its separate feasibility checks
and leaves its pinned images in the named harness session-storage directories
under `%LOCALAPPDATA%\compareModels`.
