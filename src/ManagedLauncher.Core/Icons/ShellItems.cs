using System.Runtime.InteropServices;

namespace ManagedLauncher.Core.Icons;

public static class ShellItems
{
    private const uint SIGDN_NORMALDISPLAY = 0;

    /// <summary>Name Explorer shows for a parsing name, e.g. "Microsoft Teams" for <c>shell:AppsFolder\MSTeams_…!MSTeams</c>.</summary>
    public static string? GetDisplayName(string parsingName)
    {
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var obj) != 0 || obj is not IShellItem item)
            return null;
        try
        {
            if (item.GetDisplayName(SIGDN_NORMALDISPLAY, out var ptr) != 0)
                return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(item);
        }
    }

    public sealed record StartApp(string Aumid, string Name);

    /// <summary>Every app in Explorer's Applications folder (Start menu apps, packaged and desktop), with its AUMID.</summary>
    public static IReadOnlyList<StartApp> EnumerateApps()
    {
        var result = new List<StartApp>();
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out var obj) != 0 || obj is not IShellItem folder)
            return result;
        try
        {
            var bhid = BHID_EnumItems;
            var enumIid = typeof(IEnumShellItems).GUID;
            if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref enumIid, out var enumPtr) != 0)
                return result;
            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
            Marshal.Release(enumPtr);
            try
            {
                while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        var aumid = GetName(item, SIGDN_PARENTRELATIVEPARSING);
                        var name = GetName(item, SIGDN_NORMALDISPLAY);
                        if (aumid is not null && name is not null)
                            result.Add(new StartApp(aumid, name));
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(items);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(folder);
        }
        return result;
    }

    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    private static string? GetName(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out var ptr) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems ppenum);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? item);
}
