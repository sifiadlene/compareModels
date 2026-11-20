using System.Net;
using System.Net.Sockets;
using System.Text;
using WslContainerFeasibility;
using Xunit;

namespace WslContainerFeasibility.Tests;

public sealed class LoopbackEndpointTests
{
    [Fact]
    public async Task GivenSplitHttpRequest_WhenProbed_CompleteRequestReceivesResponse()
    {
        using var endpoint = new LoopbackEndpoint();
        Task<bool> request = endpoint.WaitForRequestAsync(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n"));
        await Task.Delay(100);
        Assert.False(request.IsCompleted);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
        using var response = new StreamReader(stream, Encoding.ASCII);
        Assert.EndsWith("\r\n\r\nOK", await response.ReadToEndAsync());
        Assert.True(await request);
    }

    [Fact]
    public async Task GivenConnectionWithoutRequest_WhenProbed_ConnectionIsStillObserved()
    {
        using var endpoint = new LoopbackEndpoint();
        Task<bool> request = endpoint.WaitForRequestAsync(TimeSpan.FromSeconds(5));
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        }
        Assert.True(await request);
    }
}
