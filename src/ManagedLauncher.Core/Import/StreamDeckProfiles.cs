using System.Text.Json;

namespace ManagedLauncher.Core.Import;

public sealed record ProfileInfo(string Uuid, string Name, string? DeviceModel, string? DeviceUuid, string Directory, IReadOnlyList<string> Pages);

/// <summary>Read-only access to the Stream Deck application's installed profiles.</summary>
public static class StreamDeckProfiles
{
    public static string ProfilesRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elgato", "StreamDeck", "ProfilesV3");

    /// <summary>Models with the 15-key 5×3 layout: original Stream Deck and MK.2.</summary>
    public static readonly IReadOnlySet<string> FifteenKeyModels =
        new HashSet<string>(["20GAA9901", "20GAA9902", "20GBA9901"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ProfileInfo> List(string? root = null)
    {
        root ??= ProfilesRoot;
        if (!Directory.Exists(root))
            return [];
        var result = new List<ProfileInfo>();
        foreach (var dir in Directory.EnumerateDirectories(root, "*.sdProfile"))
        {
            var info = TryRead(dir);
            if (info is not null)
                result.Add(info);
        }
        return result;
    }

    /// <summary>Finds a profile by UUID (any case) or exact name.</summary>
    public static ProfileInfo? Find(string uuidOrName, string? root = null) =>
        List(root).FirstOrDefault(p => string.Equals(p.Uuid, uuidOrName, StringComparison.OrdinalIgnoreCase))
        ?? List(root).FirstOrDefault(p => string.Equals(p.Name, uuidOrName, StringComparison.OrdinalIgnoreCase));

    public static ProfileInfo? TryRead(string dir)
    {
        var manifest = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifest))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            var device = root.TryGetProperty("Device", out var d) ? d : default;
            var pages = root.TryGetProperty("Pages", out var p) && p.TryGetProperty("Pages", out var list)
                ? list.EnumerateArray().Select(e => e.GetString()!).ToList()
                : [];
            return new ProfileInfo(
                Path.GetFileNameWithoutExtension(dir),
                root.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                device.ValueKind == JsonValueKind.Object && device.TryGetProperty("Model", out var m) ? m.GetString() : null,
                device.ValueKind == JsonValueKind.Object && device.TryGetProperty("UUID", out var u) ? u.GetString() : null,
                dir,
                pages);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Path of page <paramref name="pageNumber"/> (1-based, in the profile's page order).</summary>
    public static string? PageManifest(ProfileInfo profile, int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > profile.Pages.Count)
            return null;
        var path = Path.Combine(profile.Directory, "Profiles", profile.Pages[pageNumber - 1].ToUpperInvariant(), "manifest.json");
        return File.Exists(path) ? path : null;
    }
}
