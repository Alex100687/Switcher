using System.Runtime.InteropServices;

namespace LayoutFix;

/// <summary>Sends synthetic keystrokes and layout-change requests to the foreground window.</summary>
public static class Injector
{
    /// <summary>Marker in dwExtraInfo so our own events are recognised by the hook.</summary>
    public static readonly IntPtr Signature = (IntPtr)0x4C46_4958; // "LFIX"

    private static readonly int InputSize = Marshal.SizeOf<Native.INPUT>();

    /// <summary>Ask the target window to switch to the given keyboard layout.</summary>
    public static void SwitchLayout(IntPtr hwnd, IntPtr hkl)
    {
        if (hkl == IntPtr.Zero) return;
        var target = FocusWindow(hwnd);
        Native.PostMessage(target, Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        if (target != hwnd)
            Native.PostMessage(hwnd, Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
    }

    /// <summary>
    /// Erase <paramref name="backspaces"/> characters, type <paramref name="text"/> and optionally press a trailing key
    /// (space / enter / tab). Everything goes out in one SendInput call so the user's next keystrokes cannot interleave.
    /// </summary>
    public static void Replace(int backspaces, string text, int trailingVk = 0)
    {
        var list = new List<Native.INPUT>(backspaces * 2 + text.Length * 2 + 2);
        for (int i = 0; i < backspaces; i++) AddVk(list, Native.VK_BACK);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\r': break;
                case '\n': AddVk(list, Native.VK_RETURN); break;
                case '\t': AddVk(list, Native.VK_TAB); break;
                default: AddUnicode(list, ch); break;
            }
        }
        if (trailingVk != 0) AddVk(list, trailingVk);
        Send(list);
    }

    public static void PressKey(int vk) => Send(new List<Native.INPUT>(2).Also(l => AddVk(l, vk)));

    private static void Send(List<Native.INPUT> list)
    {
        if (list.Count == 0) return;
        var arr = list.ToArray();
        uint sent = Native.SendInput((uint)arr.Length, arr, InputSize);
        if (sent != arr.Length)
            Log.Write($"SendInput sent {sent}/{arr.Length}, error {Marshal.GetLastWin32Error()}");
    }

    private static void AddVk(List<Native.INPUT> list, int vk)
    {
        uint scan = Native.MapVirtualKeyEx((uint)vk, 0 /*MAPVK_VK_TO_VSC*/, IntPtr.Zero);
        list.Add(Key((ushort)vk, (ushort)scan, 0));
        list.Add(Key((ushort)vk, (ushort)scan, Native.KEYEVENTF_KEYUP));
    }

    private static void AddUnicode(List<Native.INPUT> list, char ch)
    {
        list.Add(Key(0, ch, Native.KEYEVENTF_UNICODE));
        list.Add(Key(0, ch, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
    }

    private static Native.INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION
        {
            ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = Signature }
        }
    };

    /// <summary>The window that actually has keyboard focus inside the foreground window's thread (falls back to hwnd).</summary>
    public static IntPtr FocusWindow(IntPtr hwnd)
    {
        uint tid = Native.GetWindowThreadProcessId(hwnd, out _);
        var gti = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (tid != 0 && Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero)
            return gti.hwndFocus;
        return hwnd;
    }

    private static List<T> Also<T>(this List<T> list, Action<List<T>> f) { f(list); return list; }
}
