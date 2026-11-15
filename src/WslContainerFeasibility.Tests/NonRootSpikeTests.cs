using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.WSL.Containers;
using WslContainerFeasibility;
using Xunit;
using Xunit.Abstractions;
using static WslContainerFeasibility.ContainerOperations;

namespace WslContainerFeasibility.Tests;

public sealed class NonRootSpikeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GivenNonPrivilegedContainers_WhenPrivilegesAreDropped_BothRolesShareWorkspaceWithoutRoot()
    {
        string image = Environment.GetEnvironmentVariable("NONROOT_SPIKE_IMAGE") ?? "";
        string imageId = Environment.GetEnvironmentVariable("NONROOT_SPIKE_IMAGE_ID") ?? "";
        Assert.Matches(@"^[^\s]+@sha256:[0-9a-f]{64}$", image);
        Assert.Matches(@"^sha256:[0-9a-f]{64}$", imageId);
        string runId = Guid.NewGuid().ToString("N");
        string sessionName = $"CompareModels-NonRoot-{runId}";
        string directory = Path.Combine(Path.GetTempPath(), $"compareModels-nonroot-{runId}");
        string workspace = Path.Combine(directory, "workspace");
        string storage = Path.Combine(directory, "session");
        string evidencePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "compareModels", "Feasibility", "Evidence", $"nonroot-{runId}.txt");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(storage);
        var evidence = new StringBuilder($"Windows={Environment.OSVersion}\nImage={image}\nImageId={imageId}\n");
        using var session = new Session(new SessionSettings(sessionName, storage)
        {
            CpuCount = 1,
            MemorySizeInMB = 512
        });
        var serviceVersion = WslcService.GetVersion();
        var installedVersion = new Version(
            (int)serviceVersion.Major, (int)serviceVersion.Minor, (int)serviceVersion.Revision);
        evidence.AppendLine($"WslContainerService={installedVersion}");
        Assert.Equal(new Version(3, 0, 5), installedVersion);
        bool started = false;
        try
        {
            session.Start();
            started = true;
            PullImageOptions pullOptions = new(image);
            string? registryAuth = Environment.GetEnvironmentVariable("NONROOT_SPIKE_REGISTRY_AUTH");
            if (!string.IsNullOrEmpty(registryAuth))
            {
                pullOptions.RegistryAuth = registryAuth;
            }
            await session.PullImageAsync(pullOptions).AsTask().WaitAsync(TimeSpan.FromMinutes(3));
            await InContainerAsync(["CHOWN", "FOWNER", "DAC_OVERRIDE", "SETUID", "SETGID"], async container =>
            {
                await CheckAsync(container,
                    ["/bin/sh", "-ec",
                     "stat -c 'raw mount uid=%u gid=%g mode=%a' /workspace; " +
                     "stat -f -c 'workspace fstype=%T' /workspace; " +
                     "chown 1000:1000 /workspace; chmod 0770 /workspace; " +
                     "stat -c 'provisioned mount uid=%u gid=%g mode=%a' /workspace; echo PROVISIONED"],
                    0, "PROVISIONED");
                await CheckAsync(container,
                    ["/usr/bin/setpriv", "--reuid", "1001", "--regid", "1001", "--clear-groups",
                     "/bin/sh", "-ec",
                     "test \"$(id -u)\" = 1001; " +
                     "if test -r /workspace || test -w /workspace || test -x /workspace; then " +
                     "exit 1; fi; echo UID_1001_DENIED"],
                    0, "UID_1001_DENIED");
            });

            string[] minimum = ["SETUID", "SETGID", "SETPCAP"];
            await InContainerAsync(minimum, async container =>
            {
                await CheckAsync(container, ["/opt/spike/check-state"], 1, "uid=0 ");
                await CheckAsync(container,
                    ["/usr/bin/setpriv", "--reuid", "1000", "--regid", "1000", "--clear-groups",
                     "--no-new-privs", "/opt/spike/check-state"], 1, "CapBnd=00000000000001c0");
                await CheckAsync(container,
                    ["/usr/bin/setpriv", "--inh-caps=+setuid", "/usr/bin/setpriv",
                     "--ambient-caps=+setuid", "--bounding-set=-all", "--reuid", "1000",
                     "--regid", "1000", "--clear-groups", "--no-new-privs", "/opt/spike/check-state"],
                    1, "CapEff=0000000000000080 CapBnd=0000000000000000");
                await CheckAsync(container,
                    ["/usr/bin/setpriv", "--reuid", "1000", "--regid", "1000", "--clear-groups",
                     "/usr/local/bin/setuid-probe"], 0, "ruid=1000 euid=0");
                foreach (string command in new[] { "sudo -n /usr/bin/id -u", "su root -c '/usr/bin/id -u'" })
                {
                    await CheckAsync(container,
                        ["/usr/bin/setpriv", "--reuid", "1000", "--regid", "1000", "--clear-groups",
                         "/bin/sh", "-ec", $"test \"$({command})\" = 0; echo ESCALATION_CONTROL_OK"],
                        0, "ESCALATION_CONTROL_OK");
                }
            });

            foreach (string omitted in minimum)
            {
                await InContainerAsync(minimum.Where(cap => cap != omitted).ToArray(), async container =>
                {
                    await CheckAsync(container, ["/opt/spike/drop-workload", "/opt/spike/check-state"],
                        omitted == "SETPCAP" ? 1 : 127,
                        omitted == "SETPCAP" ? "CapBnd=00000000000000c0" : "setpriv:");
                });
            }

            foreach (string role in new[] { "agent", "executor" })
            {
                await InContainerAsync(minimum, async container =>
                {
                    await CheckAsync(container, ["/opt/spike/drop-workload", "/opt/spike/probe", role],
                        0, $"PROBE_OK={role}");
                    if (role == "executor")
                    {
                        await CheckAsync(container,
                            ["/opt/spike/drop-workload", "/bin/sh", "-ec",
                             "test \"$(cat /workspace/agent)\" = \"$(printf 'agent\\nexecutor')\"; " +
                             "test \"$(cat /workspace/executor)\" = executor; " +
                             "printf 'agent\\n' >> /workspace/executor; echo SHARED_WORKSPACE_OK"],
                            0, "SHARED_WORKSPACE_OK");
                    }
                }, dropInit: true);
            }

            Assert.Equal("agent\nexecutor\n", await File.ReadAllTextAsync(Path.Combine(workspace, "agent")));
            Assert.Equal("executor\nagent\n", await File.ReadAllTextAsync(Path.Combine(workspace, "executor")));
            evidence.AppendLine("WINDOWS_SPIKE_OK");
        }
        finally
        {
            try
            {
                if (started)
                {
                    session.Terminate();
                }
                session.Dispose();
                Directory.Delete(directory, recursive: true);
                evidence.AppendLine("CLEANUP_OK");
            }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
                await File.WriteAllTextAsync(evidencePath, evidence.ToString());
                output.WriteLine($"Evidence: {evidencePath}");
            }
        }

        async Task InContainerAsync(string[] capabilities, Func<Container, Task> check, bool dropInit = false)
        {
            var settings = new ContainerSettings(image)
            {
                Name = $"nonroot-{Guid.NewGuid():N}",
                Privileged = false,
                CapabilityDrops = ["ALL"],
                CapabilityAdditions = capabilities,
                NetworkingMode = ContainerNetworkingMode.None,
                EnableAutoRemove = false,
                EnableGpu = false,
                Volumes = [new ContainerVolume(workspace, "/workspace", false)],
                NamedVolumes = [],
                PortMappings = [],
                InitProcess = new ProcessSettings
                {
                    CommandLine = dropInit
                        ? ["/opt/spike/drop-workload", "/bin/sleep", "600"]
                        : ["/bin/sleep", "600"],
                    OutputMode = ProcessOutputMode.Event
                }
            };
            evidence.AppendLine($"ConfiguredPrivileged={settings.Privileged}");
            using var container = session.CreateContainer(settings);
            try
            {
                container.Start();
                string inspection = container.Inspect();
                evidence.AppendLine(inspection);
                using var inspect = JsonDocument.Parse(inspection);
                var root = inspect.RootElement;
                var host = root.GetProperty("HostConfig");
                if (host.TryGetProperty("Privileged", out var sdkPrivileged))
                {
                    Assert.Equal(JsonValueKind.False, sdkPrivileged.ValueKind);
                    evidence.AppendLine($"SdkInspectedPrivileged={sdkPrivileged}");
                }
                else
                {
                    evidence.AppendLine("SdkInspectedPrivileged=UNREPORTED");
                }
                string containerId = root.GetProperty("Id").GetString()!;
                string containerName = root.GetProperty("Name").GetString()!;
                Assert.Matches("^[0-9a-f]{64}$", containerId);
                Assert.Equal($"/{settings.Name}", containerName);
                var daemonInspection = await InspectContainerThroughDaemonAsync(
                    sessionName, containerId, evidence);
                Assert.True(daemonInspection.ExitCode == 0,
                    $"Same-session Docker inspect failed: {daemonInspection.StandardError}{daemonInspection.StandardOutput}");
                string[] daemonFields = daemonInspection.StandardOutput.Trim().Split('|');
                Assert.Equal(3, daemonFields.Length);
                Assert.Equal(containerId, daemonFields[0]);
                Assert.Equal(containerName, daemonFields[1]);
                Assert.Equal("false", daemonFields[2]);
                evidence.AppendLine($"EffectivePrivileged={daemonFields[2]}");
                var config = root.GetProperty("Config");
                if (config.TryGetProperty("Entrypoint", out var entrypoint)
                    && entrypoint.ValueKind != JsonValueKind.Null)
                {
                    Assert.Equal(JsonValueKind.Array, entrypoint.ValueKind);
                    Assert.Empty(entrypoint.EnumerateArray());
                }
                Assert.Equal(settings.InitProcess.CommandLine,
                    config.GetProperty("Cmd").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal("none", host.GetProperty("NetworkMode").GetString());
                Assert.Equal(new[] { "ALL" },
                    host.GetProperty("CapDrop").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal(capabilities.Order(),
                    host.GetProperty("CapAdd").EnumerateArray()
                        .Select(value => value.GetString() is { } cap && cap.StartsWith("CAP_", StringComparison.Ordinal)
                            ? cap[4..] : value.GetString()).Order());
                Assert.Equal(imageId, root.GetProperty("Image").GetString());
                Assert.Equal(ContainerState.Running, container.State);
                await CheckAsync(container,
                    ["/bin/sh", "-ec",
                     "uname -r; " +
                     "awk '/^(CapEff|CapBnd|Seccomp|NoNewPrivs):/ { print \"init \" $0 }' /proc/1/status; " +
                     "stat -f -c 'workspace fstype=%T' /workspace; echo INIT_STATE_RECORDED"],
                    0, "init CapBnd:");
                await check(container);
            }
            finally
            {
                StopAndDeleteContainer(container);
            }
        }

        async Task CheckAsync(Container container, string[] command, int expectedExitCode, string marker)
        {
            var result = await WslFixtureRunner.RunProcessAsync(
                container, command, TimeSpan.FromSeconds(20), CancellationToken.None,
                requiredMarker: expectedExitCode == 0 ? marker : null);
            string transcript = $"$ {string.Join(' ', command)}\nexit={result.ExitCode}\n" +
                                result.StandardOutput + result.StandardError;
            evidence.AppendLine(transcript);
            output.WriteLine(transcript);
            Assert.Equal(expectedExitCode, result.ExitCode);
            Assert.Contains(marker, result.StandardOutput + result.StandardError);
            if (expectedExitCode != 0)
            {
                Assert.DoesNotContain("STATE_OK", result.StandardOutput);
            }
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> InspectContainerThroughDaemonAsync(
        string sessionName,
        string containerId,
        StringBuilder evidence)
    {
        string wslcPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wslc.exe");
        if (!File.Exists(wslcPath))
        {
            evidence.AppendLine($"DaemonInspectFailure=Missing CLI: {wslcPath}");
            throw new FileNotFoundException("The WSL container CLI required for daemon inspection is missing.", wslcPath);
        }

        var startInfo = new ProcessStartInfo(wslcPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[]
        {
            "--session", sessionName, "system", "session", "run",
            "docker", "inspect", "--format", "{{.Id}}|{{.Name}}|{{.HostConfig.Privileged}}", containerId
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                evidence.AppendLine("DaemonInspectFailure=Process did not start.");
                throw new InvalidOperationException("The WSL container CLI did not start.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            evidence.AppendLine($"DaemonInspectFailure={exception.Message}");
            throw new InvalidOperationException("Could not start the WSL container CLI.", exception);
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            string timedOutOutput = await standardOutput;
            string timedOutError = await standardError;
            evidence.AppendLine("DaemonInspectFailure=Timed out after 15 seconds.");
            evidence.AppendLine($"DaemonInspectOutput={timedOutOutput.Trim()}");
            if (!string.IsNullOrWhiteSpace(timedOutError))
            {
                evidence.AppendLine($"DaemonInspectError={timedOutError.Trim()}");
            }
            throw new TimeoutException("The same-session Docker daemon inspection timed out.");
        }

        string output = await standardOutput;
        string error = await standardError;
        evidence.AppendLine($"DaemonInspectExitCode={process.ExitCode}");
        evidence.AppendLine($"DaemonInspectOutput={output.Trim()}");
        if (!string.IsNullOrWhiteSpace(error))
        {
            evidence.AppendLine($"DaemonInspectError={error.Trim()}");
        }
        return (process.ExitCode, output, error);
    }
}
