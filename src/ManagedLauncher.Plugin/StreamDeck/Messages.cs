using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManagedLauncher.Plugin.StreamDeck;

/// <summary>Outgoing Stream Deck SDK v2 messages, as JSON strings.</summary>
public static class Messages
{
    /// <summary>Applies to both the hardware key and the software preview.</summary>
    private const int TargetBoth = 0;

    public static string Register(string registerEvent, string uuid) =>
        Serialize(new JsonObject { ["event"] = registerEvent, ["uuid"] = uuid });

    /// <param name="title">Null omits the title, which makes Stream Deck show the title the user typed for the key.</param>
    public static string SetTitle(string context, string? title)
    {
        var payload = new JsonObject { ["target"] = TargetBoth };
        if (title is not null)
            payload["title"] = title;
        return Serialize(new JsonObject { ["event"] = "setTitle", ["context"] = context, ["payload"] = payload });
    }

    /// <param name="pngBase64">Base64 PNG without the data URI prefix; null restores the manifest image.</param>
    public static string SetImage(string context, string? pngBase64) =>
        Serialize(new JsonObject
        {
            ["event"] = "setImage",
            ["context"] = context,
            ["payload"] = new JsonObject
            {
                ["image"] = pngBase64 is null ? null : "data:image/png;base64," + pngBase64,
                ["target"] = TargetBoth,
            },
        });

    /// <param name="profile">Bundled profile name as in the manifest; null returns to the previous profile.</param>
    public static string SwitchToProfile(string context, string device, string? profile)
    {
        var payload = new JsonObject();
        if (profile is not null)
            payload["profile"] = profile;
        return Serialize(new JsonObject { ["event"] = "switchToProfile", ["context"] = context, ["device"] = device, ["payload"] = payload });
    }

    public static string ShowOk(string context) => Simple("showOk", context);

    public static string ShowAlert(string context) => Simple("showAlert", context);

    public static string SetSettings(string context, JsonNode settings) =>
        Serialize(new JsonObject { ["event"] = "setSettings", ["context"] = context, ["payload"] = settings.DeepClone() });

    public static string SendToPropertyInspector(string action, string context, JsonNode payload) =>
        Serialize(new JsonObject
        {
            ["event"] = "sendToPropertyInspector",
            ["action"] = action,
            ["context"] = context,
            ["payload"] = payload.DeepClone(),
        });

    public static string LogMessage(string message) =>
        Serialize(new JsonObject { ["event"] = "logMessage", ["payload"] = new JsonObject { ["message"] = message } });

    private static string Simple(string ev, string context) =>
        Serialize(new JsonObject { ["event"] = ev, ["context"] = context });

    private static string Serialize(JsonObject o) => o.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
