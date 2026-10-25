using System.Diagnostics;
using System.Runtime.InteropServices;
using ManagedLauncher.Core;
using ManagedLauncher.Core.Icons;
using ManagedLauncher.Core.Press;
using System.Windows;
using System.Windows.Interop;

namespace ManagedLauncher.Manager;

public partial class App : Application
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;

    private static Mutex? singleInstance;

    /// <summary>STA thread for shell icon extraction; thread-pool threads get generic icons for some files.</summary>
    internal static StaWorker Shell { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstance = new Mutex(true, @"Local\com.fulgan.managedapplauncher.manager", out var first);
        if (!first)
        {
            FocusOtherInstance();
            Shutdown();
            return;
        }

        AppPaths.MigrateLegacy();
        DispatcherUnhandledException += (_, args) =>
        {
            if (MainWindow is MainWindow { IsLoaded: true } main)
                _ = main.ShowErrorAsync("Something went wrong", args.Exception.Message);
            else
                MessageBox.Show(args.Exception.Message, "Managed App Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => DarkTitleBar((Window)sender)));
        base.OnStartup(e);
    }

    private static void FocusOtherInstance()
    {
        using var self = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(self.ProcessName);
        var pids = others.Select(p => p.Id).Where(id => id != self.Id).ToHashSet();
        foreach (var p in others)
            p.Dispose();
        var window = WindowFinder.FindMainWindow(pids);
        if (window != IntPtr.Zero)
            WindowFinder.ForceForeground(window);
    }

    /// <summary>Dark caption matching the black window background; ignored on Windows versions without these attributes.</summary>
    private static void DarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var on = 1;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int));
        var black = 0x000000;
        DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref black, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
