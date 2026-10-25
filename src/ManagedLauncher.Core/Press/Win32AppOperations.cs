using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ManagedLauncher.Core.Config;
using ManagedLauncher.Core.Processes;
using ManagedLauncher.Core.Targets;

namespace ManagedLauncher.Core.Press;

public sealed class Win32AppOperations(Action<string>? log = null) : IAppOperations
{
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_CANCELLED = 1223;

    public OperationResult Launch(ResolvedTarget target, LaunchOptions options, bool elevated)
    {
        try
        {
            if (target.Kind == TargetKind.Aumid)
                return elevated ? OperationResult.Fail("packaged apps can't run as admin") : ActivatePackaged(target.Aumid!, options.Args);

            using var started = Process.Start(BuildStartInfo(target, options, elevated));
            return OperationResult.Started(started?.Id);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return OperationResult.Fail("elevation cancelled");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or COMException)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// A shortcut is launched as-is so Explorer applies its arguments and working directory. When the preset adds
    /// arguments or a working directory, the shortcut's exe is started directly with both combined.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(ResolvedTarget target, LaunchOptions options, bool elevated)
    {
        string file;
        string? args = options.Args;
        string? workDir = options.WorkDir is null ? null : TargetResolver.Expand(options.WorkDir);

        if (target.Kind == TargetKind.Lnk && target.ShortcutExe is not null && (options.Args is not null || options.WorkDir is not null))
        {
            file = target.ShortcutExe;
            args = string.Join(' ', new[] { target.ShortcutArgs, options.Args }.Where(a => !string.IsNullOrWhiteSpace(a)));
            workDir ??= target.ShortcutWorkDir is null ? null : TargetResolver.Expand(target.ShortcutWorkDir);
        }
        else
        {
            file = target.LaunchFile ?? throw new InvalidOperationException("target has no launch file");
            if (target.Kind is not (TargetKind.Lnk or TargetKind.Url))
                workDir ??= Path.GetDirectoryName(file);
        }

        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            Arguments = args ?? "",
        };
        if (workDir is not null)
            info.WorkingDirectory = workDir;
        if (elevated)
            info.Verb = "runas";
        return info;
    }

    private static OperationResult ActivatePackaged(string aumid, string? args)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            var hr = manager.ActivateApplication(aumid, args, 0, out var pid);
            return hr == 0 ? OperationResult.Started((int)pid) : OperationResult.Fail($"activation failed 0x{hr:X8}");
        }
        finally
        {
            Marshal.FinalReleaseComObject(manager);
        }
    }

    public bool TryFocus(IReadOnlyList<ProcessInfo> processes)
    {
        var window = WindowFinder.FindMainWindow(processes.Select(p => p.Pid).ToHashSet());
        if (window == IntPtr.Zero)
            return false;
        WindowFinder.ForceForeground(window);
        return true;
    }

    /// <summary>
    /// Asks each process to close via WM_CLOSE on its windows, waits, then kills what is left. Never escalates:
    /// a process the unelevated plugin may not terminate (an elevated app) is reported, not killed. Processes
    /// under the Windows folder are never touched, and a process is only acted on while its start time proves
    /// it is the one the snapshot saw.
    /// </summary>
    public async Task<OperationResult> CloseAsync(IReadOnlyList<ProcessInfo> processes, TimeSpan timeout)
    {
        var candidates = processes.Where(p => !IsSystemProcess(p)).ToList();
        if (candidates.Count == 0)
            return OperationResult.Fail("only Windows system processes matched; nothing closed");
        var live = candidates.Select(Open).OfType<Process>().ToList();
        try
        {
            foreach (var p in live)
                WindowFinder.PostClose(p.Id);

            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    await Task.WhenAll(live.Select(p => p.WaitForExitAsync(cts.Token))).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or Win32Exception or InvalidOperationException)
                {
                }
            }

            var denied = 0;
            foreach (var p in live.Where(p => !HasExited(p)))
            {
                try
                {
                    p.Kill();
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_ACCESS_DENIED)
                {
                    denied++;
                }
                catch (InvalidOperationException)
                {
                }
            }
            return denied == 0 ? OperationResult.Ok : OperationResult.Fail($"{denied} process(es) run as admin and can't be closed from the deck");
        }
        finally
        {
            foreach (var p in live)
                p.Dispose();
        }
    }

    private static readonly string WindowsFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static bool IsSystemProcess(ProcessInfo info) =>
        info.ExePath is null || info.ExePath.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase);

    private Process? Open(ProcessInfo info)
    {
        try
        {
            var p = Process.GetProcessById(info.Pid);
            if (StartTimeMatches(p, info))
                return p;
            p.Dispose();
        }
        catch (ArgumentException)
        {
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log?.Invoke($"cannot open pid {info.Pid}: {ex.Message}");
        }
        return null;
    }

    /// <summary>Guards against a recycled pid: the process must provably be the one the snapshot saw.</summary>
    private static bool StartTimeMatches(Process p, ProcessInfo info)
    {
        try
        {
            return Math.Abs((p.StartTime.ToUniversalTime() - info.StartTime).TotalSeconds) < 1;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);

        [PreserveSig]
        int ActivateForFile(string appUserModelId, IntPtr itemArray, string verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol(string appUserModelId, IntPtr itemArray, out uint processId);
    }
}
