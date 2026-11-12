---
title: WSL container feasibility harness
description: Run controlled Windows checks against native WSL container boundaries.
---

## Purpose

This Windows-only harness exercises the native `Microsoft.WSL.Containers` API
with a pinned Alpine image. It checks synthetic host-file and environment
canaries and compares access to a local test endpoint from bridged and
no-network containers. Cancellation and timeout checks stop a container with a
child process writing only to a temporary synthetic heartbeat directory, then
verify that writes cease after container stop returns. An independent control
session and container verify that terminating the app-owned session leaves
other sessions running and usable. The control container is created by the
harness before the app-owned session, not a pre-existing user container.

The harness does not use real credentials, install WSL components, or make
elevated system changes. It mounts only the synthetic heartbeat directory in
the process-lifecycle checks. It cleans up its own containers and terminates
both named WSL sessions. The pinned image remains in session storage at
`%LOCALAPPDATA%\compareModels\WslContainerFeasibility` and
`%LOCALAPPDATA%\compareModels\WslContainerFeasibilityControl`.

These checks do not prove resistance to deliberate container escapes, general
host-file isolation, or protection of real credentials. They do not cover
WinUI, Copilot SDK authentication, MCP, application restart cleanup, or
preservation of pre-existing foreign containers. The harness-created control
container is not a pre-existing user container.

## Run

Run from PowerShell on Windows with WSL Containers available:

```powershell
dotnet run --project src/WslContainerFeasibility/WslContainerFeasibility.csproj
```

The harness reports a failure if WSL prerequisites are missing or incompatible,
the bridged control cannot reach the local endpoint, or a no-network container
can reach it. It also reports failure if a child continues writing after
cancellation or timeout stops its container, or if cleaning up the
app-owned session affects the separate control container. It does not install
missing prerequisites automatically.
