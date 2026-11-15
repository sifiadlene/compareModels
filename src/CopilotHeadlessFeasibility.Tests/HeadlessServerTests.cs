using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using GitHub.Copilot;

namespace CopilotHeadlessFeasibility.Tests;

public sealed class HeadlessServerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerRejectsWrongOrMissingConnectionToken(bool missing)
    {
        string? executable = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        Assert.True(Path.IsPathFullyQualified(executable ?? "") && File.Exists(executable),
            "Set COPILOT_CLI_PATH to the verified CLI 1.0.112 binary.");

        string home = Path.Combine(Path.GetTempPath(), $"copilot-headless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string url = $"http://127.0.0.1:{port}";
        var start = new ProcessStartInfo(executable!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "--headless", "--port", port.ToString(), "--no-auto-update", "--log-level", "none" })
            start.ArgumentList.Add(arg);
        start.Environment.Clear();
        start.Environment["HOME"] = home;
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["COPILOT_CONNECTION_TOKEN"] = token;

        using var server = Process.Start(start)!;
        Task stdout = server.StandardOutput.ReadToEndAsync();
        Task stderr = server.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            GetStatusResponse? status = null;
            while (status is null && !timeout.IsCancellationRequested)
            {
                Assert.False(server.HasExited, "CLI exited before becoming ready.");
                try
                {
                    await using var valid = Connect(url, token);
                    await valid.StartAsync(timeout.Token);
                    status = await valid.GetStatusAsync(timeout.Token);
                }
                catch (SocketException) when (!timeout.IsCancellationRequested)
                {
                    await Task.Delay(200, timeout.Token);
                }
            }
            Assert.NotNull(status);
            Assert.Equal("1.0.112", status.Version);
            Assert.Equal(3, status.ProtocolVersion);

            await using var invalid = Connect(url, missing ? null : "incorrect-connection-token");
            var error = await Assert.ThrowsAsync<IOException>(() => invalid.StartAsync(timeout.Token));
            Assert.Contains("AUTHENTICATION_FAILED", error.Message);
        }
        finally
        {
            if (!server.HasExited)
                server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            Directory.Delete(home, recursive: true);
        }
    }

    private static CopilotClient Connect(string url, string? token) =>
        new(new CopilotClientOptions { Connection = RuntimeConnection.ForUri(url, token) });
}
