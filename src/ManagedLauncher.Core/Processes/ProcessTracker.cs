using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Processes;

/// <summary>
/// Polls the process list once per interval and reports, per subscribed key, when its target
/// starts or stops running. Targets are re-resolved on every tick.
/// </summary>
public sealed class ProcessTracker : IAsyncDisposable
{
    private readonly IProcessSource source;
    private readonly TimeProvider time;
    private readonly Dictionary<string, Subscription> subscriptions = [];
    private readonly Lock gate = new();
    private readonly CancellationTokenSource stop = new();
    private Task? loop;

    public ProcessTracker(IProcessSource source, TimeProvider? time = null)
    {
        this.source = source;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised with (key, isRunning) after a tick in which the key's state changed.</summary>
    public event Action<string, bool>? RunningChanged;

    public event Action<Exception>? Error;

    public ProcessSnapshot Current { get; private set; } = ProcessSnapshot.Empty;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(1500);

    public void Start()
    {
        loop ??= Task.Run(() => RunAsync(stop.Token));
    }

    /// <summary>Adds or replaces a key. Its first state is reported on the next tick.</summary>
    public void Subscribe(string key, Func<ResolvedTarget?> resolve)
    {
        lock (gate)
            subscriptions[key] = new Subscription(resolve);
    }

    public void Unsubscribe(string key)
    {
        lock (gate)
            subscriptions.Remove(key);
    }

    /// <summary>Takes a fresh snapshot now; used after launching or killing so the indicator catches up quickly.</summary>
    public ProcessSnapshot Refresh()
    {
        var snapshot = new ProcessSnapshot(source.Snapshot());
        Apply(snapshot);
        return snapshot;
    }

    internal void Apply(ProcessSnapshot snapshot)
    {
        Current = snapshot;
        List<(string Key, bool Running)> changes = [];
        lock (gate)
        {
            foreach (var (key, sub) in subscriptions)
            {
                bool running;
                try
                {
                    running = snapshot.IsRunning(sub.Resolve());
                }
                catch (Exception ex)
                {
                    Error?.Invoke(ex);
                    continue;
                }
                if (sub.Running != running)
                {
                    sub.Running = running;
                    changes.Add((key, running));
                }
            }
        }
        foreach (var (key, running) in changes)
            RunningChanged?.Invoke(key, running);
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
            try
            {
                await Task.Delay(Interval, time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
            await loop.ConfigureAwait(false);
        stop.Dispose();
    }

    private sealed class Subscription(Func<ResolvedTarget?> resolve)
    {
        public Func<ResolvedTarget?> Resolve { get; } = resolve;
        public bool? Running { get; set; }
    }
}
