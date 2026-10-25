namespace ManagedLauncher.Core.Processes;

/// <summary>
/// Polls the process list once per interval and reports, per subscribed key, when its <see cref="RunState"/>
/// changes. Each key's state is recomputed from the snapshot on every tick.
/// </summary>
public sealed class ProcessTracker : IAsyncDisposable
{
    private readonly IProcessSource source;
    private readonly TimeProvider time;
    private readonly Dictionary<string, Subscription> subscriptions = [];
    private readonly Lock gate = new();
    private readonly Lock refreshGate = new();
    private readonly CancellationTokenSource stop = new();
    private Task? loop;

    public ProcessTracker(IProcessSource source, TimeProvider? time = null)
    {
        this.source = source;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised with (key, state) after a tick in which the key's state changed.</summary>
    public event Action<string, RunState>? StateChanged;

    public event Action<Exception>? Error;

    public ProcessSnapshot Current { get; private set; } = ProcessSnapshot.Empty;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(1500);

    public void Start()
    {
        loop ??= Task.Run(() => RunAsync(stop.Token));
    }

    /// <summary>Adds or replaces a key. Its first state is reported on the next tick.</summary>
    public void Subscribe(string key, Func<ProcessSnapshot, RunState> state)
    {
        lock (gate)
            subscriptions[key] = new Subscription(state);
    }

    public void Unsubscribe(string key)
    {
        lock (gate)
            subscriptions.Remove(key);
    }

    /// <summary>
    /// Takes a fresh snapshot now; used after launching or killing so the indicator catches up quickly. Calls are
    /// serialized so an older snapshot is never applied over a newer one.
    /// </summary>
    public ProcessSnapshot Refresh()
    {
        lock (refreshGate)
        {
            var snapshot = new ProcessSnapshot(source.Snapshot());
            Apply(snapshot);
            return snapshot;
        }
    }

    internal void Apply(ProcessSnapshot snapshot)
    {
        Current = snapshot;
        List<(string Key, RunState State)> changes = [];
        lock (gate)
        {
            foreach (var (key, sub) in subscriptions)
            {
                RunState state;
                try
                {
                    state = sub.State(snapshot);
                }
                catch (Exception ex)
                {
                    Error?.Invoke(ex);
                    continue;
                }
                if (sub.Last != state)
                {
                    sub.Last = state;
                    changes.Add((key, state));
                }
            }
        }
        foreach (var (key, state) in changes)
            StateChanged?.Invoke(key, state);
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

    private sealed class Subscription(Func<ProcessSnapshot, RunState> state)
    {
        public Func<ProcessSnapshot, RunState> State { get; } = state;
        public RunState? Last { get; set; }
    }
}
