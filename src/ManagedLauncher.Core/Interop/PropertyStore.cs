using System.Runtime.InteropServices;

namespace ManagedLauncher.Core.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey(Guid formatId, uint propertyId)
{
    public Guid FormatId = formatId;
    public uint PropertyId = propertyId;
}

internal static class PropertyKeys
{
    public static readonly PropertyKey AppUserModelId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
}

/// <summary>Minimal PROPVARIANT: only string values (VT_LPWSTR) are read.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    private const ushort VT_LPWSTR = 31;

    [FieldOffset(0)] private ushort vt;
    [FieldOffset(8)] private IntPtr pointer;

    public readonly string? GetString() => vt == VT_LPWSTR ? Marshal.PtrToStringUni(pointer) : null;

    public void Clear() => PropVariantClear(ref this);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint cProps);
    [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
    [PreserveSig] int Commit();
}
