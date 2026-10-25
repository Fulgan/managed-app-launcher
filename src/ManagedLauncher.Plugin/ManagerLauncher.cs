using System.Diagnostics;
using ManagedLauncher.Core.Press;
using ManagedLauncher.Core.Processes;

namespace ManagedLauncher.Plugin;

/// <summary>
/// Opens the manager window: brings a running instance to the front, otherwise starts one and brings its window
/// to the front once it shows. The plugin runs in the background, so a window it starts would otherwise open
/// behind the active app. Calls are serialized so presses in quick succession start at most one instance.
/// </summary>
/// <param name="start">Starts the exe and returns its pid, or null when it could not start.</param>
/// <param name="focus">Brings the pid's main window to the front; false while it has no window yet.</param>
public sealed class ManagerLauncher(
    string exePath,
    IProcessSource processes,
    Func<string, int?> start,
    Func<int, bool> focus,
    Action<string> log)
{
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WindowPoll = TimeSpan.FromMilliseconds(100);

    private readonly object gate = new();
    private readonly string exePath = Path.GetFullPath(exePath);

    public static ManagerLauncher ForDesktop(string exePath, Action<string> log) =>
        new(exePath, new Win32ProcessSource(), StartProcess(log), FocusProcess, log);

    /// <summary>Returns false only when the manager could not be started.</summary>
    public bool Open()
    {
        lock (gate)
        {
            var running = processes.Snapshot()
                .Where(p => string.Equals(p.ExePath, exePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (running.Count > 0)
            {
                if (!running.Any(p => focus(p.Pid)))
                    _ = FocusWhenShownAsync(running[0].Pid);
                return true;
            }

            if (start(exePath) is not { } pid)
                return false;
            _ = FocusWhenShownAsync(pid);
            return true;
        }
    }

    private async Task FocusWhenShownAsync(int pid)
    {
        var deadline = DateTime.UtcNow + WindowWait;
        while (DateTime.UtcNow < deadline)
        {
            if (focus(pid))
                return;
            await Task.Delay(WindowPoll).ConfigureAwait(false);
        }
        log($"manager {pid}: no window to bring to front");
    }

    private static Func<string, int?> StartProcess(Action<string> log) => path =>
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path) });
            return process?.Id;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log($"cannot start manager {path}: {ex.Message}");
            return null;
        }
    };

    private static bool FocusProcess(int pid)
    {
        var window = WindowFinder.FindMainWindow(new HashSet<int> { pid });
        if (window == IntPtr.Zero)
            return false;
        WindowFinder.ForceForeground(window);
        return true;
    }
}
