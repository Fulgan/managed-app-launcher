using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ManagedLauncher.Core.Processes;

/// <summary>
/// Enumerates processes with Toolhelp and queries each with PROCESS_QUERY_LIMITED_INFORMATION, which
/// an unelevated caller is granted even for elevated targets. Results are cached per (pid, creation time)
/// so a poll only queries processes that started since the previous one.
/// </summary>
public sealed class Win32ProcessSource : IProcessSource
{
    private readonly Dictionary<int, ProcessInfo> cache = [];
    private readonly Lock gate = new();

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        lock (gate)
        {
            var pids = EnumeratePids();
            var result = new List<ProcessInfo>(pids.Count);
            var seen = new HashSet<int>();
            foreach (var (pid, parent) in pids)
            {
                if (pid is 0 or 4 || !seen.Add(pid))
                    continue;
                var info = Query(pid, parent);
                if (info is not null)
                    result.Add(info);
            }
            foreach (var stale in cache.Keys.Where(k => !seen.Contains(k)).ToList())
                cache.Remove(stale);
            return result;
        }
    }

    private ProcessInfo? Query(int pid, int parent)
    {
        using var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid)
            return null;
        if (!Native.GetProcessTimes(handle, out var creation, out _, out _, out _))
            return null;
        var start = DateTime.FromFileTimeUtc(creation);

        if (cache.TryGetValue(pid, out var cached) && cached.StartTime == start)
            return cached;

        var info = new ProcessInfo(pid, QueryPath(handle), QueryAumid(handle), start, QueryElevated(handle), parent);
        cache[pid] = info;
        return info;
    }

    private static string? QueryPath(SafeProcessHandle handle)
    {
        var sb = new StringBuilder(1024);
        var size = sb.Capacity;
        return Native.QueryFullProcessImageNameW(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
    }

    /// <summary>A token the caller may not even query belongs to a more privileged process; that counts as elevated.</summary>
    private static bool QueryElevated(SafeProcessHandle handle)
    {
        if (!Native.OpenProcessToken(handle, Native.TOKEN_QUERY, out var token))
            return true;
        using (token)
            return !Native.GetTokenInformation(token, Native.TokenElevation, out var elevated, sizeof(int), out _) || elevated != 0;
    }

    private static string? QueryAumid(SafeProcessHandle handle)
    {
        uint length = 0;
        var rc = Native.GetApplicationUserModelId(handle, ref length, null);
        if (rc != Native.ERROR_INSUFFICIENT_BUFFER)
            return null;
        var sb = new StringBuilder((int)length);
        rc = Native.GetApplicationUserModelId(handle, ref length, sb);
        return rc == 0 ? sb.ToString() : null;
    }

    private static List<(int Pid, int Parent)> EnumeratePids()
    {
        using var snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var pids = new List<(int, int)>(512);
        var entry = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
        if (!Native.Process32FirstW(snap, ref entry))
            return pids;
        do
        {
            pids.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
        } while (Native.Process32NextW(snap, ref entry));
        return pids;
    }

    internal sealed class SafeProcessHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    private static class Native
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint TH32CS_SNAPPROCESS = 0x2;
        public const int ERROR_INSUFFICIENT_BUFFER = 122;
        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenElevation = 20;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? aumid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeProcessHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(SafeProcessHandle token, int infoClass, out int info, int length, out int returned);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Process32FirstW(SafeSnapshotHandle snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool Process32NextW(SafeSnapshotHandle snapshot, ref PROCESSENTRY32W entry);
    }

    internal sealed class SafeSnapshotHandle() : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }
}
