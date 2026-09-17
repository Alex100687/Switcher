using System.Runtime.InteropServices;

namespace LayoutFix;

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

    /// <summary>Debug aid: with LAYOUTFIX_ACCEPT_INJECTED=1 only our own output is ignored, other synthetic input is processed.</summary>
    private static readonly bool AcceptInjected = Environment.GetEnvironmentVariable("LAYOUTFIX_ACCEPT_INJECTED") == "1";

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
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                bool injected = k.dwExtraInfo == Injector.Signature || (!AcceptInjected && (k.flags & Native.LLKHF_INJECTED) != 0);
                bool extended = (k.flags & 0x01) != 0;
                try
                {
                    if (KeyDown?.Invoke(new KeyEventArgs(k.vkCode, k.scanCode, injected, extended)) == true)
                        return (IntPtr)1;
                }
                catch (Exception ex)
                {
                    Log.Write("Hook handler error: " + ex);
                }
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
