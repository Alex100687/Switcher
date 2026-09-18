using System.Diagnostics;
using System.Windows.Forms;

namespace Switcher;

/// <summary>Ties the hook, the word tracker, the corrector and the injector together.</summary>
public sealed class Engine : IDisposable
{
    private readonly Settings _settings;
    private readonly Exceptions _exceptions;
    private readonly Dictionaries _dicts;
    private readonly Corrector _corrector;
    private readonly SpellFixer _speller;
    /// <summary>
    /// Deferred injections. Injected input is processed in the injecting thread's context while the real input
    /// thread keeps delivering the user's keys, so the only moment a SendInput is atomic against typing is inside
    /// the hook callback of a hardware key. Hence: a ready fix waits for the next key the user presses and is
    /// applied right before it; if the user has paused, it is applied via a trigger event instead (nothing to
    /// interleave with). Either way the work runs on the hook thread.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _deferred = new();
    private long _lastHardwareKeyTicks;
    private static readonly TimeSpan IdleGap = TimeSpan.FromMilliseconds(150);

    private void RunInHook(Action a)
    {
        _deferred.Enqueue(a);
        var sinceKey = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref _lastHardwareKeyTicks));
        if (sinceKey > IdleGap) { Injector.SendTrigger(); return; }
        // the user is typing: the next key's callback will drain the queue; if no key comes, fall back to a trigger
        Task.Delay(IdleGap).ContinueWith(_ => { if (!_deferred.IsEmpty) Injector.SendTrigger(); });
    }
    private void DrainDeferred() { while (_deferred.TryDequeue(out var a)) SafeRun(a); }
    private void OnTrigger() => DrainDeferred();
    private readonly KeyboardHook _hook;
    private readonly WordTracker _word = new();
    private readonly Hotkey _hotkey;
    private readonly PasswordDetector _passwords = new();
    private readonly Dictionary<uint, string> _processNames = new();
    private readonly object _lock = new();

    /// <summary>
    /// Bumped whenever the text after the last word boundary stops being "just the letters in <see cref="_word"/>":
    /// another boundary, navigation, mouse click, window change, a backspace into the previous word.
    /// A pending spell fix compares it to know whether it still knows what is on screen.
    /// </summary>
    private int _epoch;
    private void Invalidate() { Interlocked.Increment(ref _epoch); _word.Reset(); }

    /// <summary>Language of the last real words typed in the current window — the prior for ambiguous words.</summary>
    private readonly Dictionary<IntPtr, int> _context = new();
    private int ContextFor(IntPtr hwnd) { lock (_lock) return _context.TryGetValue(hwnd, out var l) ? l : 0; }
    private void SetContext(IntPtr hwnd, int lang)
    {
        lock (_lock)
        {
            if (_context.Count > 64) _context.Clear();
            _context[hwnd] = lang;
        }
    }

    /// <summary>The last finished word — what is on screen now and what it would be in the other layout.</summary>
    private sealed record LastWord(string Text, IntPtr Layout, string AltText, IntPtr AltLayout,
        int TrailingVk, IntPtr Hwnd, DateTime Time, bool WasAuto, string OriginalCore);
    private LastWord? _last;

    public event Action<string>? Notify;

    public Engine(Settings settings, Exceptions exceptions, Dictionaries dicts, Frequencies freq, SpellFixer speller)
    {
        _speller = speller;
        _settings = settings;
        _exceptions = exceptions;
        _dicts = dicts;
        _corrector = new Corrector(dicts, exceptions, settings, freq);
        _hotkey = Hotkey.Parse(settings.Hotkey);
        _hook = new KeyboardHook { KeyDown = OnKeyDown, MouseDown = () => { Invalidate(); _passwords.Touch(Native.GetForegroundWindow()); }, Trigger = OnTrigger };
    }

    public void Start() => _hook.Install();

    // ------------------------------------------------------------------ key handling

    private static readonly bool Debug = Environment.GetEnvironmentVariable("SWITCHER_DEBUG") == "1";

    private bool OnKeyDown(KeyEventArgs e)
    {
        if (Debug) Log.Write($"key vk={e.Vk:X2} scan={e.Scan:X2} injected={e.Injected} fg={Native.GetForegroundWindow():X} layout={(long)Layouts.Current(Native.GetForegroundWindow()):X8} buf={_word.Count}");
        if (e.Injected) return false; // our own output (or another tool's) — never react to it

        Volatile.Write(ref _lastHardwareKeyTicks, DateTime.UtcNow.Ticks);
        if (KeyboardHook.InHardwareCallback && !_deferred.IsEmpty) DrainDeferred(); // a ready fix goes in ahead of this key

        uint vk = e.Vk;

        if (_hotkey.Matches(vk) && _settings.Enabled)
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
            SafeRun(() => HandleHotkey(hk, keys, wordLayout)); // inside the callback: injection is atomic here
            return true;
        }

        if (!_settings.Enabled || !_dicts.IsLoaded) return false;

        // A modifier pressed on its own (Alt+Shift, Ctrl+Shift, Win — the layout switch itself) keeps the word;
        // a real key with Ctrl/Alt/Win held is a shortcut and abandons it. Win+Space is the layout switch too.
        bool winDown = Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN);
        if (IsModifierKey(vk)) return false;
        if (winDown && vk == Native.VK_SPACE) return false;
        if (Native.IsDown(Native.VK_CONTROL) || Native.IsDown(Native.VK_MENU) || winDown)
        {
            Invalidate();
            return false;
        }

        var hwnd = Native.GetForegroundWindow();
        var layout = Layouts.Current(hwnd);
        _passwords.Touch(hwnd); // background refresh; never blocks

        if (!_word.IsEmpty && hwnd != _word.Hwnd) Invalidate();
        else if (!_word.IsEmpty && layout != IntPtr.Zero && layout != _word.Layout)
        {
            // the user switched the layout by hand in the middle of a word ("ult", Alt+Shift, continues typing):
            // if the letters so far make a word in the new language, flip them right away
            if (OnManualLayoutSwitch(hwnd, layout, vk, e.Scan)) return true;
        }

        if (WordTracker.IsWordKey(vk))
        {
            if (layout == IntPtr.Zero) { Invalidate(); return false; }
            _word.Push(vk, e.Scan, layout, hwnd);
            return false;
        }

        if (vk == Native.VK_BACK)
        {
            if (_word.IsEmpty) Invalidate(); // deleting into the previous word — we no longer know what's there
            else _word.Backspace();
            return false;
        }

        if (vk is Native.VK_SPACE or Native.VK_RETURN or Native.VK_TAB)
        {
            if (_word.IsEmpty) return false;
            // Shift+Enter etc. — let it through untouched, but the word is finished.
            if (vk != Native.VK_SPACE && Native.IsDown(Native.VK_SHIFT)) { Invalidate(); return false; }
            return OnWordBoundary((int)vk, hwnd);
        }

        // navigation, escape, delete, function keys… — word is abandoned
        Invalidate();
        return false;
    }

    /// <summary>Ask the window to switch, then make sure it did; some apps ignore the request — press the system hotkey.</summary>
    private DateTime _lastToggle = DateTime.MinValue;
    private void SwitchLayoutVerified(IntPtr hwnd, IntPtr target)
    {
        int before = Native.LangId(Layouts.Current(hwnd));
        Injector.SwitchLayout(hwnd, target);
        if (!_settings.ToggleHotkeyIfIgnored || before == 0 || before == Native.LangId(target)) return;
        Task.Delay(400).ContinueWith(_ => RunInHook(() => SafeRun(() =>
        {
            if (Native.GetForegroundWindow() != hwnd) return;
            // compare languages, not HKL handles (the same layout can come back as a different handle);
            // act only if the app still sits in the *old* language, and not more than once in a while
            int now = Native.LangId(Layouts.Current(hwnd));
            if (Debug) Log.Write($"verify layout: before={before:X4} target={Native.LangId(target):X4} now={now:X4} hkl={(long)Layouts.Current(hwnd):X8}");
            if (now != before || Layouts.Installed().Length != 2) return;
            if (DateTime.UtcNow - _lastToggle < TimeSpan.FromSeconds(2)) return;
            _lastToggle = DateTime.UtcNow;
            Log.Write("layout request ignored by the app — sending the toggle hotkey");
            Injector.SendToggleHotkey();
        })));
    }

    private static bool IsModifierKey(uint vk) => vk is Native.VK_SHIFT or Native.VK_LSHIFT or Native.VK_RSHIFT
        or Native.VK_CONTROL or Native.VK_LCONTROL or Native.VK_RCONTROL or Native.VK_MENU or Native.VK_LMENU or Native.VK_RMENU
        or Native.VK_LWIN or Native.VK_RWIN or Native.VK_CAPITAL;

    /// <summary>
    /// Layout changed while a word is being typed. If the word reads as a known word in the new language (and not in
    /// the old one), retype it and carry on; the current key is swallowed and re-sent after, so order is preserved.
    /// Returns true when the key was handled here.
    /// </summary>
    private bool OnManualLayoutSwitch(IntPtr hwnd, IntPtr newLayout, uint vk, uint scan)
    {
        var oldLayout = _word.Layout;
        var keys = _word.Snapshot();
        int oldLang = Native.LangId(oldLayout), newLang = Native.LangId(newLayout);
        string typed = WordTracker.Render(keys, oldLayout);
        string flipped = WordTracker.Render(keys, newLayout);
        var core = Corrector.StripPunctuation(typed, out _, out _);
        var flippedCore = Corrector.StripPunctuation(flipped, out _, out _);

        bool flip = _settings.AutoSwitchLayout && !_word.HasDigits && !IsExcluded(hwnd)
                    && flippedCore.Length >= 2 && Corrector.IsWordShaped(flippedCore)
                    && _corrector.IsKnown(newLang, flippedCore);
        // a word in both languages ("ult"/"где", or an English word finished before switching for the Russian
        // that follows): same collision rule as on a word boundary — context, then frequency
        if (flip && Corrector.IsWordShaped(core) && _corrector.IsKnown(oldLang, core))
            flip = _corrector.PreferOther(core, oldLang, flippedCore, newLang, ContextFor(hwnd), out _);
        if (Debug) Log.Write($"manual switch '{typed}' → '{flipped}' flip={flip}");
        if (flip && _passwords.IsPasswordField(hwnd)) flip = false;
        if (!flip) { Invalidate(); return false; }

        Injector.Replace(typed.Length, flipped);
        _word.SetLayout(newLayout);
        SetContext(hwnd, newLang);
        Remember(flipped, newLayout, typed, oldLayout, 0, hwnd, wasAuto: true);
        ThreadPool.QueueUserWorkItem(_ => SafeRun(() => Report($"{typed} → {flipped}  [manual layout switch]")));

        // now the key that revealed the switch
        if (WordTracker.IsWordKey(vk))
        {
            _word.Push(vk, scan, newLayout, hwnd); // injected keys are invisible to our own hook
            Injector.PressKey((int)vk);
            return true;
        }
        if (vk is Native.VK_SPACE or Native.VK_RETURN or Native.VK_TAB)
        {
            if (!OnWordBoundary((int)vk, hwnd)) Injector.PressKey((int)vk);
            return true;
        }
        if (vk == Native.VK_BACK) { _word.Backspace(); Injector.PressKey(Native.VK_BACK); return true; }
        Invalidate();
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
        int epoch = Interlocked.Increment(ref _epoch);

        if (other == IntPtr.Zero || IsExcluded(hwnd)) return false;

        string typed = WordTracker.Render(keys, layout);
        string alt = WordTracker.Render(keys, other);
        if (Debug) Log.Write($"boundary typed='{typed}' alt='{alt}'");
        if (typed.Length == 0) return false;

        int typedLang = Native.LangId(layout);
        int altLang = Native.LangId(other);

        int ctx = ContextFor(hwnd);
        var decision = _corrector.Decide(typed, typedLang, alt, altLang, hasDigits, ctx);
        if (Debug) Log.Write($"decide '{typed}' ctx={Corrector.LangName(ctx)} → {decision.Kind} {decision.Reason}");

        switch (decision.Kind)
        {
            case ActionKind.None:
                if (_corrector.IsRealWord(typedLang, typed)) SetContext(hwnd, typedLang);
                Remember(typed, layout, alt, other, boundaryVk, hwnd, wasAuto: false);
                return false;

            case ActionKind.SwitchLayout:
                if (_passwords.IsPasswordField(hwnd)) return false; // never rewrite a password
                // Synchronously, right here in the hook: our replacement keystrokes must be queued before
                // whatever the user types next, otherwise a fast typist gets the two words interleaved.
                SwitchLayoutVerified(hwnd, other);
                var sws = System.Diagnostics.Stopwatch.StartNew();
                Injector.Replace(typed.Length, alt, boundaryVk);
                Log.Write($"  sync Replace took {sws.ElapsedMilliseconds} ms inCallback={KeyboardHook.InCallback}");
                SetContext(hwnd, altLang);
                Remember(alt, other, typed, layout, boundaryVk, hwnd, wasAuto: true);
                ThreadPool.QueueUserWorkItem(_ => SafeRun(() => Report($"{typed} → {alt}  [{decision.Reason}]")));
                return true;

            case ActionKind.FixSpelling:
                if (_passwords.IsPasswordField(hwnd)) return false;
                // Suggest takes ~100 ms, so this runs on a worker. A space goes through to the app right away
                // (no typing lag); Enter/Tab are held back, because in a chat Enter would send the unfixed word.
                bool hold = boundaryVk != Native.VK_SPACE;
                string boundary = boundaryVk switch { Native.VK_RETURN => "\n", Native.VK_TAB => "\t", _ => " " };
                ThreadPool.QueueUserWorkItem(_ => SafeRun(() =>
                {
                    var fix = _speller.FixEither(typed, layout, alt, other, _settings.AutoSwitchLayout, ctx);
                    RunInHook(() => ApplyFix(fix, typed, layout, alt, other, hwnd, boundaryVk, hold, boundary, epoch));
                }));
                return hold;
        }
        return false;
    }

    /// <summary>Runs inside a hook callback: every earlier key has been seen, and our SendInput is queued atomically.</summary>
    private void ApplyFix(Decision fix, string typed, IntPtr layout, string alt, IntPtr other, IntPtr hwnd,
        int boundaryVk, bool hold, string boundary, int epoch)
    {
                {
                    bool fixing = fix.Kind == ActionKind.FixSpelling && fix.NewText != typed;

                    // Meanwhile the user may have typed the first letters of the next word: fine, we erase and retype
                    // them too (in the new layout if we switch). Anything else — another word finished, a click,
                    // an arrow key, a different window — means we no longer know what is on screen: skip the fix.
                    // The snapshot is re-taken right before SendInput so a keystroke landing in between is caught.
                    IReadOnlyList<TypedKey> pending = Array.Empty<TypedKey>();
                    IntPtr pendingLayout = IntPtr.Zero;
                    var newLayout = fixing && fix.SwitchLayout ? other : layout;
                    {
                        bool known = Volatile.Read(ref _epoch) == epoch && _word.TryPending(hwnd, out pending, out pendingLayout);
                        if (Debug) Log.Write($"fix '{typed}' → {fix.Kind} '{fix.NewText}' known={known} pending={pending.Count} epoch={epoch}/{_epoch}");
                        if (!known)
                        {
                            if (hold) Injector.PressKey(boundaryVk); // best effort: at least deliver the held Enter/Tab
                            return;
                        }
                        if (pendingLayout == IntPtr.Zero) pendingLayout = layout;
                        string pendingOld = WordTracker.Render(pending, pendingLayout);
                        string pendingNew = fixing && fix.SwitchLayout ? WordTracker.Render(pending, other) : pendingOld;

                        int backspaces; string text;
                        if (fixing)
                        {
                            backspaces = typed.Length + (hold ? 0 : 1) + pendingOld.Length;
                            text = fix.NewText + boundary + pendingNew;
                        }
                        else if (hold) { backspaces = pendingOld.Length; text = boundary + pendingOld; } // held Enter/Tab goes before the new letters
                        else { backspaces = 0; text = ""; }

                        if (fixing && fix.SwitchLayout) { SwitchLayoutVerified(hwnd, other); _word.SetLayout(other); }
                        if (backspaces > 0 || text.Length > 0)
                        {
                            var swi = System.Diagnostics.Stopwatch.StartNew();
                            Injector.Replace(backspaces, text);
                            if (Debug) Log.Write($"  Replace({backspaces}, '{text}') took {swi.ElapsedMilliseconds} ms on thread {Environment.CurrentManagedThreadId}");
                        }
                    }

                    if (!fixing)
                    {
                        Remember(typed, layout, alt, other, boundaryVk, hwnd, wasAuto: false);
                        return;
                    }
                    SetContext(hwnd, Native.LangId(newLayout));
                    Remember(fix.NewText, newLayout, typed, layout, boundaryVk, hwnd, wasAuto: true);
                    ThreadPool.QueueUserWorkItem(_ => SafeRun(() => Report($"{typed} → {fix.NewText}  [{fix.Reason}]")));
                }
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
            SwitchLayoutVerified(hwnd, other);
            Injector.Replace(typed.Length, alt);
            SetContext(hwnd, Native.LangId(other));
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
        SwitchLayoutVerified(hwnd, last.AltLayout);
        Injector.Replace(last.Text.Length + trailing.Length, last.AltText + trailing);
        SetContext(hwnd, Native.LangId(last.AltLayout));

        if (last.WasAuto)
        {
            // the user disagreed with us — never touch this word again
            var learned = last.OriginalCore;
            ThreadPool.QueueUserWorkItem(_ => SafeRun(() => _exceptions.Add(learned)));
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

    private static readonly bool IgnoreExclusions = Environment.GetEnvironmentVariable("SWITCHER_NO_EXCLUDE") == "1";

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
