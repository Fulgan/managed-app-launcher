using System.IO.Compression;
using System.Text.Json.Nodes;

namespace ManagedLauncher.Core.Import;

/// <summary>
/// Builds the launcher <c>.streamDeckProfile</c>: one App Slot action without settings on every key, so the plugin
/// pages the app list across them.
/// The zip holds a single <c>&lt;PROFILE&gt;.sdProfile/</c> folder laid out like the installed profiles: its
/// <c>manifest.json</c>, then <c>Profiles/&lt;PAGE&gt;/manifest.json</c> for the slot page and for the separate,
/// empty default page that every profile carries.
/// </summary>
public static class SlotPageBuilder
{
    public const string PluginUuid = "com.fulgan.managedapplauncher";
    public const string SlotActionUuid = "com.fulgan.managedapplauncher.slot";

    public static byte[] Build(string name, string deviceModel, string? deviceUuid, int columns, int rows, string pluginVersion)
    {
        var profile = Guid.NewGuid().ToString().ToUpperInvariant();
        var page = Guid.NewGuid().ToString().ToLowerInvariant();
        var defaultPage = Guid.NewGuid().ToString().ToLowerInvariant();
        var root = new JsonObject
        {
            ["Device"] = new JsonObject { ["Model"] = deviceModel, ["UUID"] = deviceUuid ?? "" },
            ["Name"] = name,
            ["Pages"] = new JsonObject
            {
                ["Current"] = page,
                ["Default"] = defaultPage,
                ["Pages"] = new JsonArray(page),
            },
            ["Version"] = "3.0",
        };

        var actions = new JsonObject();
        for (var slot = 0; slot < columns * rows; slot++)
            actions[$"{slot % columns},{slot / columns}"] = SlotAction(pluginVersion);

        var pageManifest = new JsonObject
        {
            ["Controllers"] = new JsonArray(new JsonObject { ["Type"] = "Keypad", ["Actions"] = actions }),
            ["Icon"] = "",
            ["Name"] = "",
        };

        var emptyPage = new JsonObject
        {
            ["Controllers"] = new JsonArray(new JsonObject { ["Actions"] = null, ["Type"] = "Keypad" }),
            ["Icon"] = "",
            ["Name"] = "",
        };

        var dir = $"{profile}.sdProfile";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, $"{dir}/manifest.json", root);
            Write(zip, $"{dir}/Profiles/{page.ToUpperInvariant()}/manifest.json", pageManifest);
            Write(zip, $"{dir}/Profiles/{defaultPage.ToUpperInvariant()}/manifest.json", emptyPage);
        }
        return buffer.ToArray();
    }

    private static JsonObject SlotAction(string pluginVersion) => new()
    {
        ["ActionID"] = Guid.NewGuid().ToString(),
        ["LinkedTitle"] = true,
        ["Name"] = "App Slot",
        ["Plugin"] = new JsonObject { ["Name"] = "Managed App Launcher", ["UUID"] = PluginUuid, ["Version"] = pluginVersion },
        ["Resources"] = null,
        ["Settings"] = new JsonObject(),
        ["State"] = 0,
        ["States"] = new JsonArray(new JsonObject
        {
            ["FontFamily"] = "",
            ["FontSize"] = 11,
            ["FontStyle"] = "",
            ["FontUnderline"] = false,
            ["OutlineThickness"] = 2,
            ["ShowTitle"] = true,
            ["Title"] = "",
            ["TitleAlignment"] = "bottom",
            ["TitleColor"] = "#ffffff",
        }),
        ["UUID"] = SlotActionUuid,
    };

    private static void Write(ZipArchive zip, string path, JsonNode json)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(json.ToJsonString());
    }
}
