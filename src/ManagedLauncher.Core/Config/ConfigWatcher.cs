namespace ManagedLauncher.Core.Config;

/// <summary>
/// Keeps the latest valid config. File events are debounced because one save produces several; a file
/// that fails to parse (mid-write by another editor, or a typo) is reported and the previous config kept.
/// </summary>
public sealed class ConfigWatcher : IDisposable
{
    private readonly string path;
    private readonly TimeProvider time;
    private readonly TimeSpan debounce;
    private readonly Lock gate = new();
    private FileSystemWatcher? watcher;
    private ITimer? pending;

    public ConfigWatcher(string path, TimeProvider? time = null, TimeSpan? debounce = null)
    {
        this.path = Path.GetFullPath(path);
        this.time = time ?? TimeProvider.System;
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        Current = ConfigStore.TryLoad(this.path, out var config, out var error) ? config : new AppConfig().Normalize();
        InitialError = error;
    }

    public AppConfig Current { get; private set; }

    public string? InitialError { get; }

    public event Action<AppConfig>? Changed;

    public event Action<string>? LoadFailed;

    public void Start()
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Changed += (_, _) => Schedule();
        watcher.Created += (_, _) => Schedule();
        watcher.Renamed += (_, _) => Schedule();
        watcher.Deleted += (_, _) => Schedule();
        watcher.EnableRaisingEvents = true;
    }

    public void Schedule()
    {
        lock (gate)
        {
            pending?.Dispose();
            pending = time.CreateTimer(_ => Reload(), null, debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Reload()
    {
        if (ConfigStore.TryLoad(path, out var config, out var error))
        {
            Current = config;
            Changed?.Invoke(config);
        }
        else
        {
            LoadFailed?.Invoke(error ?? "unknown error");
        }
    }

    public void Dispose()
    {
        watcher?.Dispose();
        lock (gate)
            pending?.Dispose();
    }
}
