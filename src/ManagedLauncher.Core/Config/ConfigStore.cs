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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
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

    /// <summary>Longest wait for another writer before <see cref="Update"/> gives up.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Load, mutate, save under a lock per config file, shared by every process of the Windows session, so the manager, the CLI and parallel Explorer
    /// verb invocations never overwrite each other's changes. An exception from <paramref name="mutate"/> leaves the
    /// file untouched.
    /// </summary>
    /// <exception cref="IOException">Another writer held the lock for longer than <see cref="LockTimeout"/>.</exception>
    public static AppConfig Update(string path, Action<AppConfig> mutate)
    {
        using var mutex = new Mutex(false, LockName(path));
        try
        {
            if (!mutex.WaitOne(LockTimeout))
                throw new IOException("config.json is busy: another program is saving it");
        }
        catch (AbandonedMutexException)
        {
        }
        try
        {
            var config = Load(path);
            mutate(config);
            Save(path, config);
            return config;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>Same file, same lock, whatever spelling the path was given in.</summary>
    private static string LockName(string path)
    {
        var full = Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..16];
        return $@"Local\com.fulgan.managedapplauncher.config.{hash}";
    }
}
