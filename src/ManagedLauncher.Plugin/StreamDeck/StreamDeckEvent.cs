using System.Text.Json;

namespace ManagedLauncher.Plugin.StreamDeck;

/// <summary>An event received from the Stream Deck application. Payload shape depends on <see cref="Event"/>.</summary>
public sealed record StreamDeckEvent(string Event, string? Action, string? Context, string? Device, JsonElement Payload)
{
    public static StreamDeckEvent Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new StreamDeckEvent(
            root.GetProperty("event").GetString() ?? "",
            Str(root, "action"),
            Str(root, "context"),
            Str(root, "device"),
            root.TryGetProperty("payload", out var payload) ? payload.Clone() : default)
        {
            DeviceInfo = root.TryGetProperty("deviceInfo", out var info) ? info.Clone() : default,
        };
    }

    /// <summary>Name, type and size of the device; sent beside the payload, at the root, by <c>deviceDidConnect</c>.</summary>
    public JsonElement DeviceInfo { get; init; }

    public JsonElement? Settings =>
        Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty("settings", out var s) ? s : null;

    public bool IsInMultiAction =>
        Payload.ValueKind == JsonValueKind.Object
        && Payload.TryGetProperty("isInMultiAction", out var m)
        && m.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
