using ManagedLauncher.Core;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Icons;
using ManagedLauncher.Core.Press;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Plugin.Keys;
using ManagedLauncher.Plugin.StreamDeck;

namespace ManagedLauncher.Plugin;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var migrated = AppPaths.MigrateLegacy();
        var log = new FileLog(Path.Combine(AppPaths.ConfigDirectory, "plugin.log"));
        if (migrated.Count > 0)
            log.Write($"copied {string.Join(", ", migrated)} from {AppPaths.LegacyConfigDirectory}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write($"unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write($"unobserved: {e.Exception}");
            e.SetObserved();
        };

        RegistrationInfo registration;
        try
        {
            registration = RegistrationInfo.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            log.Write($"bad arguments ({ex.Message}); this exe is started by the Stream Deck application");
            return 2;
        }

        log.Write($"starting, port {registration.Port}");
        using var watcher = new ConfigWatcher(AppPaths.ConfigFile);
        if (watcher.InitialError is not null)
            log.Write($"config invalid, using defaults until fixed: {watcher.InitialError}");

        using var sta = new StaWorker();
        using var deck = new StreamDeckConnection(registration);
        await using var tracker = new ProcessTracker(new Win32ProcessSource())
        {
            Interval = TimeSpan.FromMilliseconds(watcher.Current.PollMs),
        };
        tracker.Error += ex => log.Write($"tracker: {ex.Message}");

        var controller = new KeyController(
            deck,
            () => watcher.Current,
            tracker,
            new PressTimer(),
            new PressExecutor(new Win32AppOperations(log.Write)),
            new ShellKeyImages(sta),
            log.Write);
        controller.PluginContext = registration.PluginUuid;
        controller.DeckSeen = seen => KnownDecks.Upsert(AppPaths.DevicesFile, seen);
        controller.AddDevices(registration.Info);
        var manager = ManagerLauncher.ForDesktop(Path.Combine(AppContext.BaseDirectory, "ManagedLauncher.Manager.exe"), log.Write);
        controller.OpenManager = manager.Open;

        watcher.Changed += config =>
        {
            log.Write("config reloaded");
            tracker.Interval = TimeSpan.FromMilliseconds(config.PollMs);
            _ = controller.OnConfigChangedAsync().ContinueWith(
                t => log.Write($"refresh failed: {t.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
        };
        watcher.LoadFailed += error => log.Write($"config invalid, keeping previous: {error}");

        try
        {
            await deck.ConnectAsync(CancellationToken.None);
            watcher.Start();
            tracker.Start();
            await deck.RunAsync(controller.HandleAsync, ex => log.Write($"event failed: {ex}"), CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Write($"fatal: {ex}");
            return 1;
        }

        log.Write("stopped");
        return 0;
    }
}
