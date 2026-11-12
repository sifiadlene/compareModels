using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WslContainerFeasibility;

internal sealed class LoopbackEndpoint : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;

    internal LoopbackEndpoint()
    {
        _stopToken = _stop.Token;
        _listener.Start();
    }

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal async Task<bool> WaitForRequestAsync(TimeSpan timeout)
    {
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCancellation.Token,
            _stopToken);
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellation.Token);
            NetworkStream stream = client.GetStream();
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response, cancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException) when (_stopToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
