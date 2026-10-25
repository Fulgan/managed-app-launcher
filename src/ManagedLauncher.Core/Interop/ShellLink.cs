using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ManagedLauncher.Core.Interop;

public sealed record ShellLinkInfo(string? TargetPath, string? Arguments, string? WorkingDirectory, string? Aumid);

/// <summary>Reads <c>.lnk</c> files through the shell's IShellLink so they resolve the way Explorer resolves them.</summary>
public static class ShellLink
{
    private const uint SLGP_RAWPATH = 0x4;

    public static ShellLinkInfo Read(string path)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);

            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            var target = sb.ToString();
            sb.Clear();
            link.GetArguments(sb, sb.Capacity);
            var args = sb.ToString();
            sb.Clear();
            link.GetWorkingDirectory(sb, sb.Capacity);
            var workDir = sb.ToString();

            return new ShellLinkInfo(NullIfBlank(target), NullIfBlank(args), NullIfBlank(workDir), ReadAumid(link));
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static string? ReadAumid(IShellLinkW link)
    {
        if (link is not IPropertyStore store)
            return null;
        var key = PropertyKeys.AppUserModelId;
        if (store.GetValue(ref key, out var value) != 0)
            return null;
        try
        {
            return NullIfBlank(value.GetString());
        }
        finally
        {
            value.Clear();
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
