namespace ManagedLauncher.Core;

/// <summary>Well-known locations shared by the plugin and the CLI.</summary>
public static class AppPaths
{
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string ConfigDirectory { get; } = Path.Combine(Roaming, "ManagedAppLauncher");

    public static string ConfigFile { get; } = Path.Combine(ConfigDirectory, "config.json");

    /// <summary>Decks the plugin has seen; written by the plugin, read by the manager.</summary>
    public static string DevicesFile { get; } = Path.Combine(ConfigDirectory, "devices.json");

    /// <summary>Settings folder of installs that ran under the name "App Launcher".</summary>
    public static string LegacyConfigDirectory { get; } = Path.Combine(Roaming, "AppLauncher");

    /// <summary>Copies the settings of an "App Launcher" install over, once: only while this install has no config yet.</summary>
    public static IReadOnlyList<string> MigrateLegacy() => MigrateLegacy(LegacyConfigDirectory, ConfigDirectory);

    /// <returns>Names of the files copied.</returns>
    public static IReadOnlyList<string> MigrateLegacy(string from, string to)
    {
        if (File.Exists(Path.Combine(to, "config.json")) || !File.Exists(Path.Combine(from, "config.json")))
            return [];
        Directory.CreateDirectory(to);
        var copied = new List<string>();
        foreach (var name in new[] { "config.json", "devices.json" })
        {
            var source = Path.Combine(from, name);
            if (!File.Exists(source))
                continue;
            try
            {
                File.Copy(source, Path.Combine(to, name), overwrite: false);
                copied.Add(name);
            }
            catch (IOException)
            {
                // Another program starting at the same moment copied it first, or the plugin already wrote it.
            }
        }
        return copied;
    }
}
