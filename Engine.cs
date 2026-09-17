using System.Diagnostics;
using System.Windows.Forms;

namespace LayoutFix;

/// <summary>Ties the hook, the word tracker, the corrector and the injector together.</summary>
public sealed class Engine : IDisposable
{
    private readonly Settings _settings;
    private readonly Exceptions _exceptions;
    private readonly Dictionaries _dicts;
    private readonly Corrector _corrector;
    private readonly KeyboardHook _hook;
    private readonly WordTracker _word = new();
    private readonly Hotkey _hotkey;
    private readonly Dictionary<uint, string> _processNames = new();
    private readonly object _lock = new();

    /// <summary>The last finished word — what is on screen now and what it would be in the other layout.</summary>
    private sealed record LastWord(string Text, IntPtr Layout, string AltText, IntPtr AltLayout,
        int TrailingVk, IntPtr Hwnd, DateTime Time, bool WasAuto, string OriginalCore);
    private LastWord? _last;

    public event Action<string>? Notify;

    public Engine(Settings settings, Exceptions exceptions, Dictionaries dicts)
    {
        _settings = settings;
        _exceptions = exceptions;
        _dicts = dicts;
        _corrector = new Corrector(dicts, exceptions, settings);
        _hotkey = Hotkey.Parse(settings.Hotkey);
        _hook = new KeyboardHook { KeyDown = OnKeyDown, MouseDown = () => _word.Reset() };
    }

    public void Start() => _hook.Install();

    // ------------------------------------------------------------------ key handling

    private static readonly bool Debug = Environment.GetEnvironmentVariable("LAYOUTFIX_DEBUG") == "1";

    private bool OnKeyDown(KeyEventArgs e)
    {
        if (Debug) Log.Write($"key vk={e.Vk:X2} scan={e.Scan:X2} injected={e.Injected} fg={Native.GetForegroundWindow():X} layout={(long)Layouts.Current(Native.GetForegroundWindow()):X8} buf={_word.Count}");
        if (e.Injected) return false; // our own output (or another tool's) — never react to it

        uint vk = e.Vk;

        if (_hotkey.Matches(vk))
        {
            if (_settings.Enabled)
            {
                // snapshot on the hook thread, act on a worker thread
                var hk = Native.GetForegroundWindow();
                IReadOnlyList<TypedKey>? keys = null;
                IntPtr wordLayout = IntPtr.Zero;
                if (!_word.IsEmpty && _word.Hwnd == hk)
                {
                    keys = _word.Snapshot();
                    wordLayout = _word.Layout;
                }
                _word.Reset();
                ThreadPool.QueueUserWorkItem(_ => SafeRun(() => HandleHotkey(hk, keys, wordLayout)));
            }
            return true;
        }

        if (!_settings.Enabled || !_dicts.IsLoaded) return false;

        if (Native.IsDown(Native.VK_CONTROL) || Native.IsDown(Native.VK_MENU) ||
            Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN))
        {
            _word.Reset();
            return false;
        }

        if (vk is Native.VK_SHIFT or Native.VK_LSHIFT or Native.VK_RSHIFT or Native.VK_CAPITAL) return false;

        var hwnd = Native.GetForegroundWindow();
        var layout = Layouts.Current(hwnd);

        if (!_word.IsEmpty && (hwnd != _word.Hwnd || (layout != IntPtr.Zero && layout != _word.Layout)))
            _word.Reset();

        if (WordTracker.IsWordKey(vk))
        {
            if (layout == IntPtr.Zero) { _word.Reset(); return false; }
            _word.Push(vk, e.Scan, layout, hwnd);
            return false;
        }

        if (vk == Native.VK_BACK)
        {
            _word.Backspace();
            return false;
        }

        if (vk is Native.VK_SPACE or Native.VK_RETURN or Native.VK_TAB)
        {
            if (_word.IsEmpty) return false;
            // Shift+Enter etc. — let it through untouched, but the word is finished.
            if (vk != Native.VK_SPACE && Native.IsDown(Native.VK_SHIFT)) { _word.Reset(); return false; }
            return OnWordBoundary((int)vk, hwnd);
        }

        // navigation, escape, delete, function keys… — word is abandoned
        _word.Reset();
        return false;
    }

    /// <summary>Returns true if the boundary key was swallowed (we will re-send it ourselves).</summary>
    private bool OnWordBoundary(int boundaryVk, IntPtr hwnd)
    {
        var layout = _word.Layout;
        var other = Layouts.Other(layout);
        var keys = _word.Snapshot();
        bool hasDigits = _word.HasDigits;
        _word.Reset();

        if (other == IntPtr.Zero || IsExcluded(hwnd)) return false;

        string typed = WordTracker.Render(keys, layout);
        string alt = WordTracker.Render(keys, other);
        if (Debug) Log.Write($"boundary typed='{typed}' alt='{alt}'");
        if (typed.Length == 0) return false;

        int typedLang = Native.LangId(layout);
        int altLang = Native.LangId(other);

        var decision = _corrector.Decide(typed, typedLang, alt, altLang, hasDigits);

        switch (decision.Kind)
        {
            case ActionKind.None:
                Remember(typed, layout, alt, other, boundaryVk, hwnd, wasAuto: false);
                return false;

            case ActionKind.SwitchLayout:
                // Synchronously, right here in the hook: our replacement keystrokes must be queued before
                // whatever the user types next, otherwise a fast typist gets the two words interleaved.
                Injector.SwitchLayout(hwnd, other);
                Injector.Replace(typed.Length, alt, boundaryVk);
                Remember(alt, other, typed, layout, boundaryVk, hwnd, wasAuto: true);
                ThreadPool.QueueUserWorkItem(_ => SafeRun(() => Report($"{typed} → {alt}  [{decision.Reason}]")));
                return true;

            case ActionKind.FixSpelling:
                ThreadPool.QueueUserWorkItem(_ => SafeRun(() =>
                {
                    var fix = _corrector.SuggestFix(typed, layout);
                    if (fix.Kind == ActionKind.FixSpelling && fix.NewText != typed)
                    {
                        Injector.Replace(typed.Length, fix.NewText, boundaryVk);
                        Remember(fix.NewText, layout, typed, layout, boundaryVk, hwnd, wasAuto: true);
                        Report($"{typed} → {fix.NewText}  [{fix.Reason}]");
                    }
                    else
                    {
                        Injector.PressKey(boundaryVk);
                        Remember(typed, layout, alt, other, boundaryVk, hwnd, wasAuto: false);
                    }
                }));
                return true;
        }
        return false;
    }

    private void Remember(string text, IntPtr layout, string alt, IntPtr altLayout, int trailingVk, IntPtr hwnd, bool wasAuto)
    {
        // After Enter the word may be gone (chat message sent) — nothing to undo.
        var lw = trailingVk == Native.VK_RETURN
            ? null
            : new LastWord(text, layout, alt, altLayout, trailingVk, hwnd, DateTime.UtcNow, wasAuto,
                Corrector.StripPunctuation(wasAuto ? alt : text, out _, out _));
        lock (_lock) _last = lw;
    }

    // ------------------------------------------------------------------ hotkey: convert / undo last word

    private void HandleHotkey(IntPtr hwnd, IReadOnlyList<TypedKey>? keys, IntPtr layout)
    {
        // Case 1: a word is being typed right now → convert it in place.
        if (keys != null && keys.Count > 0)
        {
            var other = Layouts.Other(layout);
            if (other == IntPtr.Zero) return;
            string typed = WordTracker.Render(keys, layout);
            string alt = WordTracker.Render(keys, other);
            if (typed.Length == 0 || alt.Length == 0) return;
            Injector.SwitchLayout(hwnd, other);
            Injector.Replace(typed.Length, alt);
            Remember(alt, other, typed, layout, 0, hwnd, wasAuto: false);
            Report($"[hotkey] {typed} → {alt}");
            return;
        }

        // Case 2: toggle the last finished word (this is also "undo" for an automatic change).
        LastWord? last;
        lock (_lock) last = _last;
        if (last == null || last.Hwnd != hwnd || (DateTime.UtcNow - last.Time) > TimeSpan.FromSeconds(30)) return;
        if (last.AltText.Length == 0) return;

        string trailing = last.TrailingVk switch
        {
            Native.VK_SPACE => " ",
            Native.VK_TAB => "\t",
            _ => "",
        };
        Injector.SwitchLayout(hwnd, last.AltLayout);
        Injector.Replace(last.Text.Length + trailing.Length, last.AltText + trailing);

        if (last.WasAuto)
        {
            // the user disagreed with us — never touch this word again
            _exceptions.Add(last.OriginalCore);
            Report($"[undo] {last.Text} → {last.AltText}, '{last.OriginalCore}' добавлено в исключения");
        }
        else
        {
            Report($"[hotkey] {last.Text} → {last.AltText}");
        }
        lock (_lock)
            _last = last with { Text = last.AltText, Layout = last.AltLayout, AltText = last.Text, AltLayout = last.Layout, Time = DateTime.UtcNow, WasAuto = false };
    }

    // ------------------------------------------------------------------ misc

    private static readonly bool IgnoreExclusions = Environment.GetEnvironmentVariable("LAYOUTFIX_NO_EXCLUDE") == "1";

    private bool IsExcluded(IntPtr hwnd)
    {
        if (IgnoreExclusions || _settings.ExcludedProcesses.Count == 0) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        if (!_processNames.TryGetValue(pid, out var name))
        {
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch { name = ""; }
            if (_processNames.Count > 256) _processNames.Clear();
            _processNames[pid] = name;
        }
        foreach (var ex in _settings.ExcludedProcesses)
            if (string.Equals(ex, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void Report(string message)
    {
        if (_settings.LogActions) Log.Write(message);
        if (_settings.Beep) System.Media.SystemSounds.Asterisk.Play();
        Notify?.Invoke(message);
    }

    private static void SafeRun(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Write("Action failed: " + ex); }
    }

    public void Dispose() => _hook.Dispose();
}

/// <summary>"Ctrl+Shift+Pause" → modifiers + virtual key.</summary>
public sealed class Hotkey
{
    public uint Vk { get; private init; }
    public bool Ctrl { get; private init; }
    public bool Shift { get; private init; }
    public bool Alt { get; private init; }
    public bool Win { get; private init; }

    public static Hotkey Parse(string text)
    {
        var h = new Hotkey { Vk = Native.VK_PAUSE };
        if (string.IsNullOrWhiteSpace(text)) return h;
        bool ctrl = false, shift = false, alt = false, win = false;
        uint vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "win": win = true; break;
                default:
                    if (Enum.TryParse<Keys>(raw, ignoreCase: true, out var k)) vk = (uint)k;
                    else Log.Write($"Unknown hotkey part '{raw}', using Pause");
                    break;
            }
        }
        if (vk == 0) return h;
        return new Hotkey { Vk = vk, Ctrl = ctrl, Shift = shift, Alt = alt, Win = win };
    }

    public bool Matches(uint vk)
    {
        if (vk != Vk) return false;
        if (Ctrl != Native.IsDown(Native.VK_CONTROL)) return false;
        if (Shift != Native.IsDown(Native.VK_SHIFT)) return false;
        if (Alt != Native.IsDown(Native.VK_MENU)) return false;
        if (Win != (Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN))) return false;
        return true;
    }
}
