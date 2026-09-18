using System.Runtime.InteropServices;

namespace Switcher;

public readonly record struct KeyEventArgs(uint Vk, uint Scan, bool Injected, bool Extended);

/// <summary>
/// Low-level keyboard + mouse hooks. Must be created on a thread with a message loop (the UI thread).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private IntPtr _kbHook;
    private IntPtr _mouseHook;
    // keep delegates alive — the GC must not collect them while the hooks are installed
    private readonly Native.HookProc _kbProc;
    private readonly Native.HookProc _mouseProc;

    /// <summary>Return true to swallow the key-down event.</summary>
    public Func<KeyEventArgs, bool>? KeyDown;
    public Action? MouseDown;
    /// <summary>Raised inside the hook callback of a trigger event (see <see cref="Injector.SendTrigger"/>).</summary>
    public Action? Trigger;

    /// <summary>Debug aid: with SWITCHER_ACCEPT_INJECTED=1 only our own output is ignored, other synthetic input is processed.</summary>
    private static readonly bool AcceptInjected = Environment.GetEnvironmentVariable("SWITCHER_ACCEPT_INJECTED") == "1";

    /// <summary>True while this thread is executing a low-level keyboard hook callback.</summary>
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

    public void Install()
    {
        var hMod = Native.GetModuleHandle(null);
        _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, hMod, 0);
        if (_kbHook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) failed");
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, hMod, 0);
        if (_mouseHook == IntPtr.Zero)
            Log.Write("Mouse hook failed: " + Marshal.GetLastWin32Error());
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        bool outer = !InCallback;
        InCallback = true;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return KeyboardProcCore(nCode, wParam, lParam); }
        finally
        {
            if (outer) InCallback = false;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > 50) Log.Write($"slow hook callback: {ms:0.0} ms (msg {(int)wParam:X}) — near the system hook timeout");
        }
    }

    private IntPtr KeyboardProcCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN || msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                if (k.dwExtraInfo == Injector.TriggerSignature)
                {
                    if (msg == Native.WM_KEYDOWN)
                    {
                        try { Trigger?.Invoke(); }
                        catch (Exception ex) { Log.Write("Trigger handler error: " + ex); }
                    }
                    return (IntPtr)1; // never reaches any app
                }
                if (msg != Native.WM_KEYDOWN && msg != Native.WM_SYSKEYDOWN) return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
                bool injected = k.dwExtraInfo == Injector.Signature || (!AcceptInjected && (k.flags & Native.LLKHF_INJECTED) != 0);
                bool extended = (k.flags & 0x01) != 0;
                bool hardware = (k.flags & Native.LLKHF_INJECTED) == 0 || (AcceptInjected && k.dwExtraInfo != Injector.Signature);
                try
                {
                    InHardwareCallback = hardware;
                    if (KeyDown?.Invoke(new KeyEventArgs(k.vkCode, k.scanCode, injected, extended)) == true)
                        return (IntPtr)1;
                }
                catch (Exception ex)
                {
                    Log.Write("Hook handler error: " + ex);
                }
                finally { InHardwareCallback = false; }
            }
        }
        return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
            {
                var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                if ((m.flags & Native.LLMHF_INJECTED) == 0)
                {
                    try { MouseDown?.Invoke(); }
                    catch (Exception ex) { Log.Write("Mouse handler error: " + ex); }
                }
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_kbHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }
}
