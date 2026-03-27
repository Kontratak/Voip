using System.Text.Json;
using Voip.Shared;
using WebSocketSharp;

namespace Voip.Client.Core.Signaling;

public sealed class SignalClient : IDisposable
{
    private WebSocket? ws;

    public event Action<SignalMessage>? OnMessage;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnConnectionError;

    public bool IsConnected => ws?.ReadyState == WebSocketState.Open;

    public void Connect(string url)
    {
        Disconnect();

        ws = new WebSocket(url);
        ws.OnOpen += (_, _) => OnConnected?.Invoke();
        ws.OnClose += (_, _) => OnDisconnected?.Invoke();
        ws.OnError += (_, e) => OnConnectionError?.Invoke(e.Message);
        ws.OnMessage += (_, e) =>
        {
            var message = JsonSerializer.Deserialize<SignalMessage>(e.Data);
            if (message is not null)
            {
                OnMessage?.Invoke(message);
            }
        };

        ws.Connect();
    }

    public void Send(SignalMessage message)
    {
        if (ws is null || ws.ReadyState != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket connection is not open.");
        }

        ws.Send(JsonSerializer.Serialize(message));
    }

    public void Disconnect()
    {
        if (ws is null)
        {
            return;
        }

        if (ws.ReadyState == WebSocketState.Open || ws.ReadyState == WebSocketState.Connecting)
        {
            ws.Close();
        }

        ws = null;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
