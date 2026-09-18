using System.Runtime.InteropServices;

namespace Switcher;

/// <summary>Sends synthetic keystrokes and layout-change requests to the foreground window.</summary>
public static class Injector
{
    /// <summary>Marker in dwExtraInfo so our own events are recognised by the hook.</summary>
    public static readonly IntPtr Signature = (IntPtr)0x4C46_4958; // "LFIX"
    /// <summary>Marker of the trigger event: "run the queued work inside the hook callback".</summary>
    public static readonly IntPtr TriggerSignature = (IntPtr)0x4C46_5452; // "LFTR"
    private const ushort TriggerVk = 0xE8; // unassigned virtual key; the hook swallows it anyway

    /// <summary>
    /// Sends a dummy key event (from a worker thread). Its hook callback is where deferred injections run: inside a
    /// low-level hook callback the input thread is waiting on us, so a SendInput there is queued atomically and
    /// every key the user pressed before it has already been seen. Calling SendInput on the hook thread *outside*
    /// a callback is 100x slower and lets other keys interleave.
    /// </summary>
    public static void SendTrigger()
    {
        var arr = new[]
        {
            new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = TriggerVk, dwExtraInfo = TriggerSignature } } },
            new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = TriggerVk, dwFlags = Native.KEYEVENTF_KEYUP, dwExtraInfo = TriggerSignature } } },
        };
        Native.SendInput((uint)arr.Length, arr, InputSize);
    }

    private static readonly int InputSize = Marshal.SizeOf<Native.INPUT>();
    private static readonly bool Diag = Environment.GetEnvironmentVariable("SWITCHER_DEBUG") == "1";

    // SendInput returns only after the input thread has run the low-level hooks for the injected events. If the
    // thread that owns our hook is the caller — or is blocked waiting for the caller — that is a deadlock resolved
    // by the hook timeout (~200 ms), during which other keys slip in between ours. So every SendInput is done by
    // this dedicated thread, fire-and-forget: the hook thread never waits for it and stays free to run callbacks.
    private static readonly System.Collections.Concurrent.BlockingCollection<(Native.INPUT[] batch, ManualResetEvent? done)> _queue = new();
    private static readonly Thread _thread = StartThread();

    private static Thread StartThread()
    {
        var t = new Thread(() =>
        {
            foreach (var (batch, done) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    uint sent = Native.SendInput((uint)batch.Length, batch, InputSize);
                    if (sent != batch.Length)
                        Log.Write($"SendInput sent {sent}/{batch.Length}, error {Marshal.GetLastWin32Error()}");
                    if (Diag) Log.Write($"  injector: SendInput({batch.Length} events) took {sw.Elapsed.TotalMilliseconds:0.0} ms");
                }
                catch (Exception ex) { Log.Write("SendInput failed: " + ex.Message); }
                finally { done?.Set(); }
            }
        }) { IsBackground = true, Name = "Switcher injector" };
        t.Start();
        return t;
    }

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
        // Plain key-down/key-up pairs for everything. (Backspace as auto-repeat - N downs, one up - would be 40%
        // fewer events, but Chromium/Electron apps ignore repeated downs without an up, and nothing gets erased.)
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
        if (!KeyboardHook.InCallback) { _queue.Add((arr, null)); return; }

        // Inside a hardware key's hook callback the input thread is blocked on us, so whatever is injected now
        // reaches the app before that key and before anything typed later. But SendInput on *this* thread would
        // crawl (~5 ms/event: each injected event wants a hook callback on the thread that is busy calling
        // SendInput). So the injector thread sends while we pump the sent messages — our own callbacks for the
        // injected events — until it is done. Typically ~1 ms for a whole word.
        using var done = new ManualResetEvent(false);
        _queue.Add((arr, done));
        var handles = new[] { done.SafeWaitHandle.DangerousGetHandle() };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done.WaitOne(0))
        {
            Native.PumpSentMessages();
            Native.MsgWaitForMultipleObjectsEx(1, handles, 20, Native.QS_SENDMESSAGE, Native.MWMO_INPUTAVAILABLE);
            if (sw.ElapsedMilliseconds > 250) { Log.Write("injection did not finish in 250 ms — giving up the wait"); break; }
        }
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

    /// <summary>
    /// Press the system layout-toggle hotkey (Alt+Shift / Ctrl+Shift per HKCU\Keyboard Layout\Toggle, else Win+Space).
    /// Used only when an app ignored WM_INPUTLANGCHANGEREQUEST and exactly two layouts are installed.
    /// </summary>
    public static void SendToggleHotkey()
    {
        string mode = "1";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Toggle");
            mode = (key?.GetValue("Language Hotkey") ?? key?.GetValue("Hotkey"))?.ToString() ?? "1";
        }
        catch { }
        var list = new List<Native.INPUT>(8);
        switch (mode)
        {
            case "2": Down(list, Native.VK_CONTROL); Down(list, Native.VK_SHIFT); Up(list, Native.VK_SHIFT); Up(list, Native.VK_CONTROL); break;
            case "3": Down(list, Native.VK_LWIN); Down(list, Native.VK_SPACE); Up(list, Native.VK_SPACE); Up(list, Native.VK_LWIN); break;
            default:  Down(list, Native.VK_MENU); Down(list, Native.VK_SHIFT); Up(list, Native.VK_SHIFT); Up(list, Native.VK_MENU); break;
        }
        Send(list);
    }

    private static void Down(List<Native.INPUT> l, int vk) => l.Add(Key((ushort)vk, (ushort)Native.MapVirtualKeyEx((uint)vk, 0, IntPtr.Zero), 0));
    private static void Up(List<Native.INPUT> l, int vk) => l.Add(Key((ushort)vk, (ushort)Native.MapVirtualKeyEx((uint)vk, 0, IntPtr.Zero), Native.KEYEVENTF_KEYUP));

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
