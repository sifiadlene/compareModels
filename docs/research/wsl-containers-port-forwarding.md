# WSL Containers port forwarding on Windows (3.0.5)

## Findings

The original Windows integration failure was not evidence that loopback
forwarding failed. The saved `Inspect()` output showed the bridged container
exited with code 127. A focused test ran `/bin/busybox httpd --help` in the
same pinned Alpine image and confirmed `httpd: applet not found`. The probe
server now uses BusyBox `nc` to serve its per-run HTTP marker.
Container-side probe scripts normalize Windows line endings to LF before
execution and distinguish a failed download from an unexpected response body.

The test initially attempted to map a port for a `NetworkMode.None` container.
WSL Containers 3.0.5 rejects that configuration: `ResolveNetworkMode` throws
`E_INVALIDARG` with "Port mappings are not supported without networking"
([`WSLCContainer.cpp`](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/wslcsession/WSLCContainer.cpp)).
The native and WinRT tests confirm this rejection
([`WslcSdkTests.cpp`](https://github.com/microsoft/WSL/blob/3.0.5/test/windows/wslc/WslcSdkTests.cpp),
[`WslcSdkWinRTTests.cpp`](https://github.com/microsoft/WSL/blob/3.0.5/test/windows/wslc/WslcSdkWinRTTests.cpp)).
The runner records this specific configuration as `Unsupported`; its separate
no-network control still verifies that a no-network container cannot reach the
bridged listener directly.

The explicit `127.0.0.1` binding is supported by WSL Containers. Microsoft's
[3.0.5 `ContainerPortMapping` documentation](https://github.com/microsoft/WSL/blob/3.0.5/doc/docs/api-reference/cpp/data-classes/containerportmapping.md)
describes the address setter, and its
[WinRT test](https://github.com/microsoft/WSL/blob/3.0.5/test/windows/wslc/WslcSdkWinRTTests.cpp)
binds a bridged mapping to `127.0.0.1` and verifies an HTTP response.

## Verification

On the Windows 11 / WSL Containers 3.0.5 environment used for this
investigation, the command below passed the forwarding integration test and
all 10 platform-independent probe tests:

```powershell
dotnet test src\WslContainerFeasibility.Tests\WslContainerFeasibility.Tests.csproj --filter "FullyQualifiedName~GivenMappedContainers_WhenForwardingIsProbed|FullyQualifiedName~WslPortForwardingProbeTests" --logger "console;verbosity=normal"
```

Result: **11 passed, 0 failed**. The integration test verifies the marker over
Windows loopback, confirms non-loopback host addresses cannot reach the mapped
port, records the unsupported `None` mapping, and verifies the no-network
control cannot reach the bridged container. This is local evidence only; it
does not establish remote reachability or firewall behavior.

## Sources

- [WSL Containers 3.0.5 release](https://github.com/microsoft/WSL/releases/tag/3.0.5)
- [ContainerPortMapping API docs](https://github.com/microsoft/WSL/blob/3.0.5/doc/docs/api-reference/cpp/data-classes/containerportmapping.md)
- [Network mode validation](https://github.com/microsoft/WSL/blob/3.0.5/src/windows/wslcsession/WSLCContainer.cpp)
- [Native SDK tests](https://github.com/microsoft/WSL/blob/3.0.5/test/windows/wslc/WslcSdkTests.cpp)
- [WinRT SDK tests](https://github.com/microsoft/WSL/blob/3.0.5/test/windows/wslc/WslcSdkWinRTTests.cpp)
