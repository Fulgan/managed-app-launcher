using Microsoft.Win32;

namespace ManagedLauncher.Core;

/// <summary>
/// Per-user "Pin to Stream Deck" context-menu entry on shortcuts and executables. On Windows 11 classic verbs
/// appear under "Show more options".
/// </summary>
public static class ShellVerb
{
    public const string VerbKey = "PinToStreamDeck";
    public const string VerbText = "Pin to Stream Deck";
    public static readonly string[] ProgIds = ["lnkfile", "exefile"];

    /// <param name="classesRoot">Registry path under HKCU; tests pass a scratch key.</param>
    public static void Install(string cliPath, string classesRoot = @"Software\Classes")
    {
        foreach (var progId in ProgIds)
        {
            using var verb = Registry.CurrentUser.CreateSubKey($@"{classesRoot}\{progId}\shell\{VerbKey}");
            verb.SetValue("", VerbText);
            verb.SetValue("Icon", $"\"{cliPath}\",0");
            using var command = verb.CreateSubKey("command");
            command.SetValue("", Command(cliPath));
        }
    }

    public static void Uninstall(string classesRoot = @"Software\Classes")
    {
        foreach (var progId in ProgIds)
            Registry.CurrentUser.DeleteSubKeyTree($@"{classesRoot}\{progId}\shell\{VerbKey}", throwOnMissingSubKey: false);
    }

    public static string Command(string cliPath) => $"\"{cliPath}\" pin \"%1\" --notify";
}
