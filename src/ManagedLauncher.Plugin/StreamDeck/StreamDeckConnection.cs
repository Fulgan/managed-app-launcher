using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ManagedLauncher.Plugin.StreamDeck;

/// <summary>Command-line arguments the Stream Deck application passes to a plugin executable.</summary>
public sealed record RegistrationInfo(int Port, string PluginUuid, string RegisterEvent, string Info)
{
    public static RegistrationInfo Parse(string[] args)
    {
        string? Get(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new RegistrationInfo(
            int.Parse(Get("-port") ?? throw new ArgumentException("missing -port")),
            Get("-pluginUUID") ?? throw new ArgumentException("missing -pluginUUID"),
            Get("-registerEvent") ?? throw new ArgumentException("missing -registerEvent"),
            Get("-info") ?? "{}");
    }
}

public interface IStreamDeck
{
    Task SetTitleAsync(string context, string? title);
    Task SetImageAsync(string context, string? pngBase64);
    Task ShowOkAsync(string context);
    Task ShowAlertAsync(string context);
    Task SetSettingsAsync(string context, JsonNode settings);
    Task SendToPropertyInspectorAsync(string action, string context, JsonNode payload);
    Task LogAsync(string message);
    Task SwitchToProfileAsync(string context, string device, string? profile);
}

/// <summary>
/// WebSocket link to the Stream Deck application. Sends are serialized because ClientWebSocket
/// allows only one outstanding send.
/// </summary>
public sealed class StreamDeckConnection(RegistrationInfo registration) : IStreamDeck, IDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public async Task ConnectAsync(CancellationToken token)
    {
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{registration.Port}"), token).ConfigureAwait(false);
        await SendAsync(Messages.Register(registration.RegisterEvent, registration.PluginUuid)).ConfigureAwait(false);
    }

    /// <summary>Dispatches events until the socket closes. Handler exceptions are reported, never propagated.</summary>
    public async Task RunAsync(Func<StreamDeckEvent, Task> handler, Action<Exception> onError, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            message.SetLength(0);
            do
            {
                result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            try
            {
                await handler(StreamDeckEvent.Parse(json)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        }
    }

    public Task SetTitleAsync(string context, string? title) => SendAsync(Messages.SetTitle(context, title));
    public Task SetImageAsync(string context, string? pngBase64) => SendAsync(Messages.SetImage(context, pngBase64));
    public Task ShowOkAsync(string context) => SendAsync(Messages.ShowOk(context));
    public Task ShowAlertAsync(string context) => SendAsync(Messages.ShowAlert(context));
    public Task SetSettingsAsync(string context, JsonNode settings) => SendAsync(Messages.SetSettings(context, settings));

    public Task SendToPropertyInspectorAsync(string action, string context, JsonNode payload) =>
        SendAsync(Messages.SendToPropertyInspector(action, context, payload));

    public Task LogAsync(string message) => SendAsync(Messages.LogMessage(message));

    public Task SwitchToProfileAsync(string context, string device, string? profile) =>
        SendAsync(Messages.SwitchToProfile(context, device, profile));

    private async Task SendAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public void Dispose()
    {
        socket.Dispose();
        sendLock.Dispose();
    }
}
