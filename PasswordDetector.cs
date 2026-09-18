using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace LayoutFix;

/// <summary>
/// Is the keyboard focus in a password box? Classic Win32 edits carry ES_PASSWORD (instant); browsers, Electron and
/// UWP expose IsPassword through UI Automation, which can stall on a busy app. So UIA runs on its own STA thread,
/// refreshed in the background (on clicks, window changes and every second while typing), and the hook only ever
/// reads the last answer. Unknown means "not a password" — the program behaves as usual.
/// </summary>
public sealed class PasswordDetector
{
    private const int GWL_STYLE = -16;
    private const int ES_PASSWORD = 0x0020;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(1);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Native.POINT pt);

    private readonly object _lock = new();
    private readonly AutoResetEvent _request = new(false);
    private IntPtr _pending;        // focus window to query next
    private bool _busy;
    private IntPtr _cachedFocus;    // answer applies to this focus window
    private bool _cachedValue;
    private DateTime _cachedAt;

    public PasswordDetector()
    {
        var t = new Thread(Worker) { IsBackground = true, Name = "LayoutFix UIA" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private static readonly bool Disabled = Environment.GetEnvironmentVariable("LAYOUTFIX_NO_UIA") == "1";
    private Native.POINT _pendingCaret;
    private bool _pendingHasCaret;

    /// <summary>Call often (every key): schedules a background refresh when the focus changed or the answer is stale.</summary>
    public void Touch(IntPtr foreground)
    {
        if (Disabled) return;
        // focus window and caret position come from GetGUIThreadInfo — no input-queue attaching involved
        uint tid = Native.GetWindowThreadProcessId(foreground, out _);
        var gti = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (tid == 0 || !Native.GetGUIThreadInfo(tid, ref gti)) return;
        var focus = gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : foreground;
        bool hasCaret = gti.hwndCaret != IntPtr.Zero;
        var caret = new Native.POINT { x = (gti.rcCaret.left + gti.rcCaret.right) / 2, y = (gti.rcCaret.top + gti.rcCaret.bottom) / 2 };
        if (hasCaret) hasCaret = ClientToScreen(gti.hwndCaret, ref caret);
        lock (_lock)
        {
            bool stale = focus != _cachedFocus || DateTime.UtcNow - _cachedAt > Ttl;
            if (!stale || _busy) return;
            _busy = true;
            _pending = focus;
            _pendingCaret = caret;
            _pendingHasCaret = hasCaret;
        }
        _request.Set();
    }

    /// <summary>Last known answer for the focused control (never blocks).</summary>
    public bool IsPasswordField(IntPtr foreground)
    {
        var focus = Injector.FocusWindow(foreground);
        if (HasPasswordStyle(focus)) return true;
        lock (_lock) return focus == _cachedFocus && _cachedValue;
    }

    private static bool HasPasswordStyle(IntPtr focus)
    {
        try
        {
            var cls = new System.Text.StringBuilder(64);
            Native.GetClassName(focus, cls, cls.Capacity);
            // "Edit", "WindowsForms10.EDIT.app…", "RichEdit20W" — anything edit-like with the password style
            return cls.ToString().Contains("EDIT", StringComparison.OrdinalIgnoreCase)
                   && (GetWindowLongW(focus, GWL_STYLE) & ES_PASSWORD) != 0;
        }
        catch { return false; }
    }

    private void Worker()
    {
        while (true)
        {
            _request.WaitOne();
            IntPtr focus; Native.POINT caret; bool hasCaret;
            lock (_lock) { focus = _pending; caret = _pendingCaret; hasCaret = _pendingHasCaret; }
            bool result = false;
            try
            {
                // NOT AutomationElement.FocusedElement: that one attaches our input queue to the target's, and
                // Windows then silently makes the target's keyboard layout equal to ours. Hit-testing the caret
                // position and asking the focus window are side-effect free.
                if (hasCaret)
                {
                    var el = AutomationElement.FromPoint(new System.Windows.Point(caret.x, caret.y));
                    if (el != null) result = el.Current.IsPassword;
                }
                if (!result)
                {
                    var el = AutomationElement.FromHandle(focus);
                    if (el != null) result = el.Current.IsPassword;
                }
            }
            catch (Exception ex) { Log.Write("UIA: " + ex.Message); }
            lock (_lock)
            {
                _cachedFocus = focus;
                _cachedValue = result;
                _cachedAt = DateTime.UtcNow;
                _busy = false;
            }
        }
    }
}
