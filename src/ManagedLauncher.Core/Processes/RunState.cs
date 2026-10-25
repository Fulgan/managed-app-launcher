namespace ManagedLauncher.Core.Processes;

/// <summary>What a key's running indicator shows.</summary>
public enum RunState
{
    Stopped,

    /// <summary>Launched from the deck moments ago and not showing a window yet.</summary>
    Starting,

    Running,

    /// <summary>Running, and at least one of its processes is elevated.</summary>
    Elevated,
}
