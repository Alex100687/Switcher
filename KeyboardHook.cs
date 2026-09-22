using System.Runtime.InteropServices;

namespace Switcher;

/// <param name="Injected">Synthetic: ours, or anyone else's (unless SWITCHER_ACCEPT_INJECTED=1).</param>
/// <param name="Foreign">Synthetic input from another program (AutoHotkey, a password manager's auto-type, another switcher).</param>
public readonly record struct KeyEventArgs(uint Vk, uint Scan, bool Injected, bool Extended, bool Foreign = false);

/// <summary>
/// Low-level keyboard + mouse hooks on a dedicated thread with its own message loop. The UI thread (tray, settings
/// window, message boxes) may block for a while; if the hooks lived there, every key and mouse move in the system
/// would wait for it. A watchdog re-installs the hooks if Windows silently removed them (it does that to a hook
/// that exceeds LowLevelHooksTimeout).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private IntPtr _kbHook;
    private IntPtr _mouseHook;
    // keep delegates alive — the GC must not collect them while the hooks are installed
    private readonly Native.HookProc _kbProc;
    private readonly Native.HookProc _mouseProc;
    private Thread? _thread;
    private uint _threadId;
    private System.Threading.Timer? _watchdog;
    private int _lastSeenTick;      // Environment.TickCount of the last event any of our hooks received
    private long _lastReinstall;    // Environment.TickCount64
    private const uint WM_REINSTALL = Native.WM_APP + 1;

    /// <summary>Return true to swallow the key-down event.</summary>
    public Func<KeyEventArgs, bool>? KeyDown;
    public Action? MouseDown;
    /// <summary>Raised inside the hook callback of a trigger event (see <see cref="Injector.SendTrigger"/>).</summary>
    public Action? Trigger;
    /// <summary>
    /// Watchdog exemption: input that our hooks legitimately do not see (a window above our integrity level is in
    /// front — UIPI hides its input from us) must not be mistaken for dead hooks.
    /// </summary>
    public Func<IntPtr, bool>? InputHiddenFrom;

    /// <summary>Debug aid: with SWITCHER_ACCEPT_INJECTED=1 only our own output is ignored, other synthetic input is processed.</summary>
    private static readonly bool AcceptInjected = Environment.GetEnvironmentVariable("SWITCHER_ACCEPT_INJECTED") == "1";

    /// <summary>True while this thread is executing a low-level keyboard or mouse hook callback.</summary>
    [ThreadStatic] public static bool InCallback;
    /// <summary>
    /// True while executing the callback of a *hardware* key: the input thread is blocked on us, so anything we
    /// SendInput now reaches the app before that key and before any key pressed later — the only atomic moment.
    /// </summary>
    [ThreadStatic] public static bool InHardwareCallback;

    public KeyboardHook()
    {
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    /// <summary>Start the hook thread and install the hooks there; throws if the keyboard hook cannot be installed.</summary>
    public void Install()
    {
        using var ready = new ManualResetEventSlim(false);
        Exception? error = null;
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            Native.PeekMessage(out _, IntPtr.Zero, 0, 0, Native.PM_NOREMOVE); // create the message queue before anyone posts to it
            try { InstallHooks(); }
            catch (Exception ex) { error = ex; ready.Set(); return; }
            ready.Set();
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_REINSTALL) { Reinstall(); continue; }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            RemoveHooks();
        }) { IsBackground = true, Name = "Switcher hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait();
        if (error != null) throw error;
        Volatile.Write(ref _lastSeenTick, Environment.TickCount);
        _watchdog = new System.Threading.Timer(_ => CheckAlive(), null, 2000, 2000);
    }

    private void InstallHooks()
    {
        var hMod = Native.GetModuleHandle(null);
        _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, hMod, 0);
        if (_kbHook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) failed");
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, hMod, 0);
        if (_mouseHook == IntPtr.Zero)
            Log.Write("Mouse hook failed: " + Marshal.GetLastWin32Error());
    }

    private void RemoveHooks()
    {
        if (_kbHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    private void Reinstall()
    {
        RemoveHooks();
        try { InstallHooks(); Log.Write("hooks re-installed (the system had stopped calling them)"); }
        catch (Exception ex) { Log.Write("hook re-install failed: " + ex.Message); }
        Volatile.Write(ref _lastSeenTick, Environment.TickCount);
    }

    /// <summary>
    /// Windows (7+) removes a low-level hook that times out, without telling anyone. Our hooks see every key and
    /// every mouse move, so if the session has had input for a while that none of them saw, they are gone.
    /// </summary>
    private void CheckAlive()
    {
        try
        {
            var lii = new Native.LASTINPUTINFO { cbSize = Marshal.SizeOf<Native.LASTINPUTINFO>() };
            if (!Native.GetLastInputInfo(ref lii)) return;
            int behind = unchecked((int)(lii.dwTime - (uint)Volatile.Read(ref _lastSeenTick)));
            if (behind < 2000) return;
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return; // secure desktop (UAC prompt, lock screen): input there is not ours to see
            if (InputHiddenFrom?.Invoke(fg) == true) return;
            if (Environment.TickCount64 - Interlocked.Read(ref _lastReinstall) < 10_000) return;
            Interlocked.Exchange(ref _lastReinstall, Environment.TickCount64);
            Log.Write($"watchdog: {behind} ms of input the hooks did not see — re-installing");
            Native.PostThreadMessage(_threadId, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Write("watchdog: " + ex.Message); }
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Volatile.Write(ref _lastSeenTick, Environment.TickCount);
        bool outer = !InCallback;
        InCallback = true;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool swallow;
        try { swallow = KeyboardProcCore(nCode, wParam, lParam); }
        finally
        {
            if (outer) InCallback = false;
            double ms = Elapsed(t0);
            if (ms > 50) Log.Write($"slow hook callback: {ms:0.0} ms (msg {(int)wParam:X}) — near the system hook timeout");
        }
        if (swallow) return (IntPtr)1;
        // Timed separately: CallNextHookEx runs the low-level hooks of other programs (Punto Switcher, macro tools…).
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        double chain = Elapsed(t1);
        if (chain > 100) Log.Write($"slow next hook: {chain:0.0} ms inside CallNextHookEx (msg {(int)wParam:X}) — another program's keyboard hook");
        return result;
    }

    private static double Elapsed(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Returns true to swallow the event.</summary>
    private bool KeyboardProcCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return false;
        int msg = (int)wParam;
        if (msg != Native.WM_KEYDOWN && msg != Native.WM_SYSKEYDOWN && msg != Native.WM_KEYUP && msg != Native.WM_SYSKEYUP) return false;
        var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        if (k.dwExtraInfo == Injector.TriggerSignature)
        {
            if (msg == Native.WM_KEYDOWN)
            {
                try { Trigger?.Invoke(); }
                catch (Exception ex) { Log.Write("Trigger handler error: " + ex); }
            }
            return true; // never reaches any app
        }
        if (msg != Native.WM_KEYDOWN && msg != Native.WM_SYSKEYDOWN) return false;
        bool synthetic = (k.flags & Native.LLKHF_INJECTED) != 0;
        bool ours = k.dwExtraInfo == Injector.Signature;
        bool injected = ours || (!AcceptInjected && synthetic);
        bool foreign = synthetic && !ours && !AcceptInjected;
        bool extended = (k.flags & 0x01) != 0;
        bool hardware = !synthetic || (AcceptInjected && !ours);
        try
        {
            InHardwareCallback = hardware;
            return KeyDown?.Invoke(new KeyEventArgs(k.vkCode, k.scanCode, injected, extended, foreign)) == true;
        }
        catch (Exception ex)
        {
            Log.Write("Hook handler error: " + ex);
            return false;
        }
        finally { InHardwareCallback = false; }
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Volatile.Write(ref _lastSeenTick, Environment.TickCount);
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
            {
                var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                if ((m.flags & Native.LLMHF_INJECTED) == 0)
                {
                    // a hardware click: the input thread waits for us, so injecting here lands before the click
                    bool outer = !InCallback;
                    InCallback = true;
                    try { MouseDown?.Invoke(); }
                    catch (Exception ex) { Log.Write("Mouse handler error: " + ex); }
                    finally { if (outer) InCallback = false; }
                }
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        if (_thread == null) return;
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (!_thread.Join(1000)) Log.Write("hook thread did not stop in 1 s");
        _thread = null;
    }
}
