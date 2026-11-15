using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WslContainerFeasibility;
using Xunit;

namespace WslContainerFeasibility.Tests;

public sealed class WslPortForwardingProbeTests
{
    [Fact]
    public async Task ConnectionProbeDistinguishesListeningAndClosedPorts()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.True(await WslPortForwardingRunner.CanConnectAsync(IPAddress.Loopback, port, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }
        Assert.False(await WslPortForwardingRunner.CanConnectAsync(IPAddress.Loopback, port, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectionProbeDoesNotTreatCancellationAsAnUnreachablePort()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WslPortForwardingRunner.CanConnectAsync(IPAddress.Loopback, 12345, cancellation.Token));
    }

    [Fact]
    public void EnumeratedHostAddressesAreNotLoopback()
    {
        Assert.All(WslPortForwardingRunner.GetNonLoopbackAddresses(), address => Assert.False(IPAddress.IsLoopback(address)));
    }

    [Theory]
    [InlineData("marker", true)]
    [InlineData("different", false)]
    public async Task HttpProbeRequiresTheExpectedListener(string responseBody, bool expected)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Task server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using NetworkStream stream = client.GetStream();
                byte[] request = new byte["GET / HTTP/1.0\r\n\r\n".Length];
                await stream.ReadExactlyAsync(request);
                byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.0 200 OK\r\n\r\n{responseBody}");
                await stream.WriteAsync(response);
            });
            if (expected)
            {
                Assert.True(await WslPortForwardingRunner.WaitForHttpAsync(IPAddress.Loopback, port, "marker", CancellationToken.None));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    WslPortForwardingRunner.WaitForHttpAsync(IPAddress.Loopback, port, "marker", CancellationToken.None));
            }
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectedHttpTimeoutIsUnverified()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await release.Task;
        });
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WslPortForwardingRunner.WaitForHttpAsync(IPAddress.Loopback, port, "marker",
                    CancellationToken.None, TimeSpan.FromSeconds(1)));
        }
        finally
        {
            release.TrySetResult();
            listener.Stop();
            await server;
        }
    }

    [Theory]
    [InlineData("127.0.0.1", "12345", true)]
    [InlineData("0.0.0.0", "12345", false)]
    [InlineData("127.0.0.1", "12346", false)]
    public void InspectRequiresTheExpectedLoopbackMapping(string ip, string port, bool expected)
    {
        string json = JsonSerializer.Serialize(new { Ports = new Dictionary<string, object>
        {
            ["18080/tcp"] = new[] { new { HostIp = ip, HostPort = port } }
        } });
        using var inspect = JsonDocument.Parse(json);
        Assert.Equal(expected, WslPortForwardingRunner.HasLoopbackMapping(inspect.RootElement, 12345));
    }

    [Fact]
    public void InspectReadsTheAddressFromTheBridgedNetwork()
    {
        using var inspect = JsonDocument.Parse("""
            {"HostConfig":{"NetworkMode":"bridge"},"NetworkSettings":{"Networks":{"bridge":{"IPAddress":"172.17.0.2"}}}}
            """);
        Assert.Equal("172.17.0.2", WslPortForwardingRunner.GetBridgedAddress(inspect.RootElement));
    }
}
