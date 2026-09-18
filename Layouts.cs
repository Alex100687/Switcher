namespace Switcher;

/// <summary>Helpers around installed keyboard layouts (HKLs).</summary>
public static class Layouts
{
    public static IntPtr[] Installed()
    {
        int n = Native.GetKeyboardLayoutList(0, null);
        if (n <= 0) return Array.Empty<IntPtr>();
        var arr = new IntPtr[n];
        Native.GetKeyboardLayoutList(n, arr);
        return arr;
    }

    /// <summary>Layout currently active in the window's thread (IntPtr.Zero if unknown).</summary>
    public static IntPtr Current(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        uint tid = Native.GetWindowThreadProcessId(hwnd, out _);
        var hkl = tid == 0 ? IntPtr.Zero : Native.GetKeyboardLayout(tid);
        if (hkl != IntPtr.Zero) return hkl;
        // some hosts (console, UWP frames) report nothing for the top-level window — ask the focused child
        var focus = Injector.FocusWindow(hwnd);
        if (focus != hwnd && focus != IntPtr.Zero)
        {
            tid = Native.GetWindowThreadProcessId(focus, out _);
            if (tid != 0) hkl = Native.GetKeyboardLayout(tid);
        }
        return hkl;
    }

    /// <summary>
    /// The layout to try "the other language" in: RU ↔ EN when both are installed, otherwise the only other layout.
    /// </summary>
    public static IntPtr Other(IntPtr current)
    {
        var all = Installed();
        int lang = Native.LangId(current);
        int want = lang == Dictionaries.LangRu ? Dictionaries.LangEn
                 : lang == Dictionaries.LangEn ? Dictionaries.LangRu : 0;
        if (want != 0)
        {
            foreach (var h in all)
                if (Native.LangId(h) == want) return h;
        }
        if (all.Length == 2)
            return Native.LangId(all[0]) == lang ? all[1] : all[0];
        return IntPtr.Zero;
    }

    public static string Name(IntPtr hkl) => Corrector.LangName(Native.LangId(hkl));
}
