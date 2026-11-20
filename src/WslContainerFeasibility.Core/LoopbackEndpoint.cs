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
        bool accepted = false;
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellation.Token);
            accepted = true;
            NetworkStream stream = client.GetStream();
            byte[] request = new byte[8192];
            int length = 0;
            while (length < request.Length)
            {
                int received = await stream.ReadAsync(request.AsMemory(length), cancellation.Token);
                if (received == 0)
                {
                    return true;
                }
                length += received;
                if (request.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
                {
                    break;
                }
            }
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response, cancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return accepted;
        }
        catch (SocketException) when (_stopToken.IsCancellationRequested)
        {
            return accepted;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
