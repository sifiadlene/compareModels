---
title: WSL container feasibility harness
description: Run controlled Windows checks against native WSL container boundaries.
---

## Purpose

This Windows-only harness exercises the native `Microsoft.WSL.Containers` API
with a pinned Alpine image. It checks synthetic host-file and environment
canaries and compares access to a local test endpoint from bridged and
no-network containers.

The harness does not use real credentials, mount host files into the
containers, install WSL components, or make elevated system changes. It creates
and cleans up only its own containers and terminates its named WSL session.
The pinned image remains in the session storage at
`%LOCALAPPDATA%\compareModels\WslContainerFeasibility`.

These checks do not prove resistance to deliberate container escapes and do
not cover WinUI, Copilot SDK credentials, MCP, descendant cancellation, or
application restart cleanup.

## Run

Run from PowerShell on Windows with WSL Containers available:

```powershell
dotnet run --project src/WslContainerFeasibility/WslContainerFeasibility.csproj
```

The harness reports a failure if WSL prerequisites are missing or incompatible,
the bridged control cannot reach the local endpoint, or a no-network container
can reach it. It does not install missing prerequisites automatically.
