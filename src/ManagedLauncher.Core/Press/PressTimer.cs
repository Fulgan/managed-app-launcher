namespace ManagedLauncher.Core.Press;

/// <summary>
/// Tells short presses from long ones per key. The long action fires from a timer while the key is still
/// held, so the user gets feedback without releasing; the release that follows is ignored.
/// </summary>
public sealed class PressTimer(TimeProvider? time = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly Dictionary<string, Held> held = [];
    private readonly Lock gate = new();

    /// <param name="longPress">Null when the key has no long action; the release then always counts as short.</param>
    public void KeyDown(string key, TimeSpan threshold, Action onShort, Action? longPress)
    {
        lock (gate)
        {
            if (held.Remove(key, out var previous))
                previous.Timer?.Dispose();

            var state = new Held(onShort);
            if (longPress is not null)
                state.Timer = time.CreateTimer(_ => FireLong(key, state, longPress), null, threshold, Timeout.InfiniteTimeSpan);
            held[key] = state;
        }
    }

    public void KeyUp(string key)
    {
        Action? shortAction = null;
        lock (gate)
        {
            if (!held.Remove(key, out var state))
                return;
            state.Timer?.Dispose();
            if (!state.LongFired)
                shortAction = state.OnShort;
        }
        shortAction?.Invoke();
    }

    /// <summary>Forgets a key that disappeared while held, without firing anything.</summary>
    public void Cancel(string key)
    {
        lock (gate)
        {
            if (held.Remove(key, out var state))
                state.Timer?.Dispose();
        }
    }

    private void FireLong(string key, Held state, Action longPress)
    {
        lock (gate)
        {
            if (!held.TryGetValue(key, out var current) || !ReferenceEquals(current, state) || state.LongFired)
                return;
            state.LongFired = true;
        }
        longPress();
    }

    private sealed class Held(Action onShort)
    {
        public Action OnShort { get; } = onShort;
        public ITimer? Timer { get; set; }
        public bool LongFired { get; set; }
    }
}
