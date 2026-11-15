using System.Net;
using System.Net.Sockets;
using System.Text;
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
}
