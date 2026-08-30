using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipPort.FnOS.Contracts;

namespace ClipPort.FnOS.Realtime;

public sealed class TaskEventHub
{
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task AcceptAsync(
        HttpContext context,
        Func<object> snapshotFactory,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        var client = new Client(socket);
        Guid id = Guid.NewGuid();
        _clients[id] = client;
        try
        {
            await SendAsync(
                client,
                new TaskEvent("snapshot", snapshotFactory(), DateTimeOffset.UtcNow),
                cancellationToken);
            byte[] buffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _clients.TryRemove(id, out _);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    public async Task PublishAsync(
        string type,
        object data,
        CancellationToken cancellationToken = default)
    {
        var message = new TaskEvent(type, data, DateTimeOffset.UtcNow);
        foreach ((Guid id, Client client) in _clients.ToArray())
        {
            try
            {
                await SendAsync(client, message, cancellationToken);
            }
            catch (Exception ex) when (
                ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                _clients.TryRemove(id, out _);
            }
        }
    }

    private async Task SendAsync(
        Client client,
        TaskEvent message,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);
        await client.SendGate.WaitAsync(cancellationToken);
        try
        {
            if (client.Socket.State == WebSocketState.Open)
            {
                await client.Socket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
        }
        finally
        {
            client.SendGate.Release();
        }
    }

    private sealed record Client(WebSocket Socket)
    {
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }
}
