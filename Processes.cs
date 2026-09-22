using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Switcher;

/// <summary>What we need to know about the process behind a window: its name and whether it runs above us.</summary>
public readonly record struct ProcInfo(string Name, bool Elevated);

/// <summary>
/// Per-pid cache of process name and integrity. A window of a process with a higher integrity level than ours
/// (an app "run as administrator") is protected by UIPI: our SendInput into it is silently dropped. Whatever we
/// swallowed there (Space, Enter, a letter to re-send) would be lost, so such windows are never rewritten.
/// </summary>
public static class Processes
{
    private static readonly Dictionary<uint, ProcInfo> _cache = new();
    private static readonly Lazy<int> OwnIntegrity = new(() => IntegrityOf(Native.GetCurrentProcess()));

    public static ProcInfo Of(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return new ProcInfo("", false);
        lock (_cache)
        {
            if (_cache.TryGetValue(pid, out var info)) return info;
        }
        var fresh = new ProcInfo(NameOf(pid), IsAboveUs(pid));
        lock (_cache)
        {
            if (_cache.Count > 256) _cache.Clear(); // pids get reused; a small cache is refreshed often enough
            _cache[pid] = fresh;
        }
        return fresh;
    }

    private static string NameOf(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return ""; }
    }

    /// <summary>True if the process runs at a higher integrity level than we do (or we cannot even tell).</summary>
    private static bool IsAboveUs(uint pid)
    {
        int own = OwnIntegrity.Value;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return true; // another user's or a protected process — not ours to type into
        try
        {
            int level = IntegrityOf(h);
            if (level >= 0) return own >= 0 && level > own;
            // the token is unreadable: processes are NO_READ_UP, so VM_READ is refused exactly for higher levels
            var probe = Native.OpenProcess(Native.PROCESS_VM_READ, false, pid);
            if (probe == IntPtr.Zero) return true;
            Native.CloseHandle(probe);
            return false;
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Mandatory integrity RID of a process (0x2000 medium, 0x3000 high…), or -1 if it cannot be read.</summary>
    private static int IntegrityOf(IntPtr process)
    {
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token)) return -1;
        try
        {
            Native.GetTokenInformation(token, Native.TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
            if (len <= 0) return -1;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buf, len, out _)) return -1;
                var sid = Marshal.ReadIntPtr(buf); // TOKEN_MANDATORY_LABEL.Label.Sid
                int count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                return count == 0 ? -1 : Marshal.ReadInt32(Native.GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { Native.CloseHandle(token); }
    }

    public static string ClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new System.Text.StringBuilder(128);
        return Native.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}
