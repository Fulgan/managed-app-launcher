using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManagedLauncher.Core.Config;

public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Missing file yields defaults. Malformed JSON throws so callers can keep their last good config.</summary>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            return new AppConfig().Normalize();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var config = JsonSerializer.Deserialize<AppConfig>(stream, JsonOptions)
            ?? throw new JsonException("config is null");
        return config.Normalize();
    }

    public static bool TryLoad(string path, out AppConfig config, out string? error)
    {
        try
        {
            config = Load(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            config = null!;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Writes to a sibling temp file and swaps it in, so a reader never sees a half-written config.
    /// </summary>
    public static void Save(string path, AppConfig config)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, config.Normalize(), JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Load, mutate, save. Used by the CLI, the only regular writer.</summary>
    public static AppConfig Update(string path, Action<AppConfig> mutate)
    {
        var config = Load(path);
        mutate(config);
        Save(path, config);
        return config;
    }
}
