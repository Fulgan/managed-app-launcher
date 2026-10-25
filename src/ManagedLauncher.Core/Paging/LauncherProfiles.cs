using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManagedLauncher.Core.Paging;

/// <param name="Name">Profile name; the manifest and <c>switchToProfile</c> use <c>profiles/{Name}</c>.</param>
/// <param name="Label">Short device name shown in the manager.</param>
/// <param name="DeviceType">Stream Deck SDK device type the profile is for.</param>
/// <param name="Model">Device model code written into the profile file.</param>
public sealed record LauncherProfile(string Name, string Label, int DeviceType, string Model, int Columns, int Rows)
{
    public string ManifestName => $"profiles/{Name}";

    public int Keys => Columns * Rows;
}

/// <summary>
/// The launcher profiles the plugin ships, one per supported device model. <c>launcher-profiles.json</c> is the only
/// place a model is listed: the build generates the profile files and the manifest's <c>Profiles</c> array from it,
/// and the plugin and the manager read it at runtime.
/// </summary>
public static class LauncherProfiles
{
    public static IReadOnlyList<LauncherProfile> All { get; } = Load();

    /// <summary>The profile for a device: by its type, else by its key layout; null when none fits.</summary>
    public static LauncherProfile? For(int? deviceType, int columns, int rows) =>
        All.FirstOrDefault(p => p.DeviceType == deviceType)
        ?? All.FirstOrDefault(p => p.Columns == columns && p.Rows == rows);

    /// <summary>
    /// The manifest's <c>Profiles</c> array: never switched to on install. Installing with the plugin is the default;
    /// <c>AutoInstall</c> stays out because the manifest schema for the declared minimum Stream Deck version rejects it.
    /// </summary>
    public static JsonArray ManifestEntries() =>
        new(All.Select(p => (JsonNode)new JsonObject
        {
            ["Name"] = p.ManifestName,
            ["DeviceType"] = p.DeviceType,
            ["Readonly"] = false,
            ["DontAutoSwitchWhenInstalled"] = true,
        }).ToArray());

    private static List<LauncherProfile> Load()
    {
        using var stream = typeof(LauncherProfiles).Assembly.GetManifestResourceStream("ManagedLauncher.Core.Paging.launcher-profiles.json")
            ?? throw new InvalidOperationException("launcher-profiles.json is not embedded");
        return JsonSerializer.Deserialize<List<LauncherProfile>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }
}
