using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ManagedLauncher.Core.Interop;

public sealed record ShellLinkInfo(string? TargetPath, string? Arguments, string? WorkingDirectory, string? Aumid);

/// <summary>Reads <c>.lnk</c> files through the shell's IShellLink so they resolve the way Explorer resolves them.</summary>
public static class ShellLink
{
    private const uint SLGP_RAWPATH = 0x4;
    private const int MsiGuidChars = 39;
    private const int MsiPathChars = 1024;
    private const int InstallStateLocal = 3;
    private const uint DarwinIdSignature = 0xA0000006;
    private const int DarwinIdUnicodeOffset = 8 + 260;

    public static ShellLinkInfo Read(string path)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);

            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            var target = AdvertisedTarget(link) ?? sb.ToString();
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

    private static string? AdvertisedTarget(IShellLinkW link)
    {
        if (link is not IShellLinkDataList data || data.CopyDataBlock(DarwinIdSignature, out var block) != 0)
            return null;
        string? descriptor;
        try
        {
            descriptor = Marshal.PtrToStringUni(block + DarwinIdUnicodeOffset);
        }
        finally
        {
            LocalFree(block);
        }

        var product = new StringBuilder(MsiGuidChars);
        var feature = new StringBuilder(MsiGuidChars);
        var component = new StringBuilder(MsiGuidChars);
        if (string.IsNullOrEmpty(descriptor) || MsiDecomposeDescriptorW(descriptor, product, feature, component, out _) != 0 || component.Length == 0)
            return null;
        var length = (uint)MsiPathChars;
        var exe = new StringBuilder(MsiPathChars);
        return MsiGetComponentPathW(product.ToString(), component.ToString(), exe, ref length) == InstallStateLocal
            ? NullIfBlank(exe.ToString())
            : null;
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

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiDecomposeDescriptorW(string descriptor, StringBuilder product, StringBuilder feature, StringBuilder component, out uint used);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetComponentPathW(string product, string component, StringBuilder path, ref uint length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1")]
    private interface IShellLinkDataList
    {
        [PreserveSig] int AddDataBlock(IntPtr dataBlock);
        [PreserveSig] int CopyDataBlock(uint signature, out IntPtr dataBlock);
        [PreserveSig] int RemoveDataBlock(uint signature);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int SetFlags(uint flags);
    }

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
