using System.Windows.Forms;

namespace Switcher;

/// <summary>Ties the hook, the word tracker, the corrector and the injector together.</summary>
public sealed class Engine : IDisposable
{
    private readonly Settings _settings;
    private readonly Rules _exceptions;
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
    private Hotkey _hotkey;
    /// <summary>Re-read the hotkey from settings (the settings window changed it).</summary>
    public void ReloadHotkey() => _hotkey = Hotkey.Parse(_settings.Hotkey);

    /// <summary>Temporary pause from the tray menu / settings window; DateTime.MinValue = not paused.</summary>
    public DateTime PausedUntil { get; set; } = DateTime.MinValue;
    public bool IsPaused => DateTime.UtcNow < PausedUntil;

    /// <summary>Raised (on a worker thread) when the same word's correction was undone often enough to offer it as a personal word.</summary>
    public event Action<string>? SuggestWord;
    private readonly HashSet<string> _suggested = new(StringComparer.OrdinalIgnoreCase);
    private readonly PasswordDetector _passwords = new();
    private readonly object _lock = new();

    /// <summary>
    /// Bumped whenever the text after the last word boundary stops being "just the letters in <see cref="_word"/>":
    /// another boundary, navigation, mouse click, window change, a backspace into the previous word.
    /// A pending spell fix compares it to know whether it still knows what is on screen.
    /// </summary>
    private int _epoch;
    /// <summary>
    /// Anything that moves the caret also forfeits the undo of the last word (Pause must never erase text elsewhere)
    /// and the password answer (the caret may now be in another field of the same window).
    /// </summary>
    private void Invalidate()
    {
        Interlocked.Increment(ref _epoch);
        _word.Reset();
        lock (_lock) _last = null;
        _passwords.Forget();
    }

    /// <summary>Set by the host once dictionaries, frequencies and the warm-up are done; nothing is processed before.</summary>
    public volatile bool Ready;
    private volatile bool _disposed;

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

    /// <summary>
    /// The last finished word — what is on screen now and what it would be in the other layout.
    /// <paramref name="TypedCore"/> is the word as the user typed it (what an undo restores and what may become a
    /// personal word); <paramref name="RejectFrom"/> is the form the corrector saw when it chose the replacement —
    /// for a fix in the other layout that is the other layout's rendering (";spym" was fixed as "жызнь" → "жизнь").
    /// </summary>
    private sealed record LastWord(string Text, IntPtr Layout, string AltText, IntPtr AltLayout,
        int TrailingVk, IntPtr Hwnd, DateTime Time, bool WasAuto, string TypedCore, string RejectFrom);
    private LastWord? _last;

    /// <summary>
    /// An Enter/Tab held back while its word's spelling fix is computed (in a chat Enter would send the unfixed word).
    /// Exactly one party delivers it: the fix (together with the corrected word), or — if the user presses another
    /// non-letter key or clicks first — that key's callback, right before the key, so the text keeps its order.
    /// </summary>
    private sealed class HeldBoundary(int vk, string text, IntPtr hwnd)
    {
        public int Vk { get; } = vk;
        public string Text { get; } = text;
        public IntPtr Hwnd { get; } = hwnd;
        private int _claimed;
        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    }
    private HeldBoundary? _held;

    /// <summary>Become the one who delivers (or deliberately drops) the held key.</summary>
    private bool TakeHeld(HeldBoundary h)
    {
        if (!h.TryClaim()) return false;
        Interlocked.CompareExchange(ref _held, null, h);
        return true;
    }

    /// <summary>
    /// Inside a hardware key/click callback: the held Enter/Tab goes in now, before this key. Letters of the next word
    /// typed meanwhile are already on screen in front of it, so they are erased and retyped after it.
    /// </summary>
    private void ReleaseHeld()
    {
        var h = Volatile.Read(ref _held);
        if (h == null || !TakeHeld(h)) return;
        if (Native.GetForegroundWindow() != h.Hwnd) { Log.Write("held key dropped: focus moved"); return; }
        if (_word.TryPending(h.Hwnd, out var pending, out var layout) && pending.Count > 0)
        {
            string letters = WordTracker.Render(pending, layout);
            Injector.Replace(letters.Length, h.Text + letters);
        }
        else Injector.PressKey(h.Vk);
        if (Debug) Log.Write("held key delivered before the next key; its fix is abandoned");
    }

    /// <summary>Fallback delivery of a held key whose fix will not happen (stale, failed) — on the hook thread, only into its own window.</summary>
    private void DeliverHeldLater(HeldBoundary? h)
    {
        if (h == null) return;
        RunInHook(() =>
        {
            if (!TakeHeld(h)) return;
            if (Native.GetForegroundWindow() == h.Hwnd) Injector.PressKey(h.Vk);
            else Log.Write("held key dropped: focus moved");
        });
    }

    public event Action<string>? Notify;

    public Engine(Settings settings, Rules exceptions, Dictionaries dicts, Frequencies freq, SpellFixer speller)
    {
        _speller = speller;
        _settings = settings;
        _exceptions = exceptions;
        _dicts = dicts;
        _corrector = new Corrector(dicts, exceptions, settings, freq);
        _hotkey = Hotkey.Parse(settings.Hotkey);
        _hook = new KeyboardHook
        {
            KeyDown = OnKeyDown,
            MouseDown = OnMouseDown,
            Trigger = OnTrigger,
            InputHiddenFrom = h => Processes.Of(h).Elevated,
        };
    }

    public void Start() => _hook.Install();

    // ------------------------------------------------------------------ key handling

    private static readonly bool Debug = Environment.GetEnvironmentVariable("SWITCHER_DEBUG") == "1";

    private void OnMouseDown()
    {
        ReleaseHeld(); // the click has not reached the app yet: a held Enter still lands where it was pressed
        Invalidate();
        _passwords.Touch(Native.GetForegroundWindow());
    }

    private bool OnKeyDown(KeyEventArgs e)
    {
        if (Debug) Log.Write($"key vk={e.Vk:X2} scan={e.Scan:X2} injected={e.Injected} foreign={e.Foreign} fg={Native.GetForegroundWindow():X} layout={(long)Layouts.Current(Native.GetForegroundWindow()):X8} buf={_word.Count}");
        if (e.Injected)
        {
            // Another program typed into the field (auto-type, a macro, another switcher): the buffer no longer
            // matches the screen, and erasing "our" word would erase theirs.
            if (e.Foreign && !IsModifierKey(e.Vk)) Invalidate();
            return false; // our own output — never react to it
        }

        uint vk = e.Vk;
        Volatile.Write(ref _lastHardwareKeyTicks, DateTime.UtcNow.Ticks);
        if (KeyboardHook.InHardwareCallback)
        {
            if (!_deferred.IsEmpty) DrainDeferred(); // a ready fix goes in ahead of this key
            // a held Enter/Tab whose fix is still being computed goes in ahead of any key that is not a letter
            if (!WordTracker.IsWordKey(vk) && !IsModifierKey(vk)) ReleaseHeld();
        }

        if (_hotkey.Matches(vk) && _settings.Enabled && Ready && !IsPaused)
        {
            var hk = Native.GetForegroundWindow();
            if (Processes.Of(hk).Elevated) return false; // our input would not reach it; let the key through
            if (_passwords.IsPasswordField(hk)) return true; // swallow, but never touch a password
            // snapshot on the hook thread, act on a worker thread
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

        if (!_settings.Enabled || !Ready || _disposed || IsPaused) return false;

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
            // A second space, an empty line: the last word is no longer right before the caret — neither an undo
            // nor a pending fix may count characters back from here.
            if (_word.IsEmpty) { Invalidate(); return false; }
            // Shift+Enter etc. — let it through untouched, but the word is finished.
            if (vk != Native.VK_SPACE && Native.IsDown(Native.VK_SHIFT)) { Invalidate(); return false; }
            bool swallowed = OnWordBoundary((int)vk, hwnd);
            if (vk != Native.VK_SPACE) _passwords.Forget(); // Tab/Enter usually moves the caret to another field
            return swallowed;
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
        RuleScope.Current = ProcessName(hwnd);
        int oldLang = Native.LangId(oldLayout), newLang = Native.LangId(newLayout);
        string typed = WordTracker.Render(keys, oldLayout);
        string flipped = WordTracker.Render(keys, newLayout);
        var core = Corrector.StripPunctuation(typed, out _, out _);
        var flippedCore = Corrector.StripPunctuation(flipped, out _, out _);

        bool flip = _settings.AutoSwitchLayout && !_word.HasDigits
                    && flippedCore.Length >= 2 && Corrector.IsWordShaped(flippedCore)
                    && _corrector.IsKnown(newLang, flippedCore) && !_exceptions.IsBlocked(core, flippedCore);
        // a word in both languages ("ult"/"где", or an English word finished before switching for the Russian
        // that follows): same collision rule as on a word boundary — context, then frequency
        if (flip && Corrector.IsWordShaped(core) && _corrector.IsKnown(oldLang, core))
            flip = _corrector.PreferOther(core, oldLang, flippedCore, newLang, ContextFor(hwnd), out _);
        if (flip && !CanRewrite(hwnd)) flip = false;
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
        lock (_lock) _last = null; // the previous word is no longer right before the caret

        if (other == IntPtr.Zero || !CanRewrite(hwnd)) return false;

        string typed = WordTracker.Render(keys, layout);
        string alt = WordTracker.Render(keys, other);
        if (Debug) Log.Write($"boundary typed='{typed}' alt='{alt}'");
        if (typed.Length == 0) return false;

        int typedLang = Native.LangId(layout);
        int altLang = Native.LangId(other);

        int ctx = ContextFor(hwnd);
        RuleScope.Current = ProcessName(hwnd); // app-specific rules apply on this thread from here on
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
                if (Debug) Log.Write($"  sync Replace took {sws.ElapsedMilliseconds} ms inCallback={KeyboardHook.InCallback}");
                SetContext(hwnd, altLang);
                Remember(alt, other, typed, layout, boundaryVk, hwnd, wasAuto: true);
                ThreadPool.QueueUserWorkItem(_ => SafeRun(() => Report($"{typed} → {alt}  [{decision.Reason}]")));
                return true;

            case ActionKind.FixSpelling:
                if (_passwords.IsPasswordField(hwnd)) return false;
                // Suggest takes ~100 ms, so this runs on a worker. A space goes through to the app right away
                // (no typing lag); Enter/Tab are held back, because in a chat Enter would send the unfixed word.
                string boundary = boundaryVk switch { Native.VK_RETURN => "\n", Native.VK_TAB => "\t", _ => " " };
                HeldBoundary? held = boundaryVk != Native.VK_SPACE ? new HeldBoundary(boundaryVk, boundary, hwnd) : null;
                if (held != null)
                {
                    var previous = Interlocked.Exchange(ref _held, held);
                    if (previous != null && TakeHeld(previous)) Injector.PressKey(previous.Vk); // never lose a user's key
                }
                var focus = Injector.FocusWindow(hwnd);
                SubmitFix(new FixRequest(typed, layout, alt, other, hwnd, focus, boundaryVk, held, boundary, epoch, ctx, RuleScope.Current));
                return held != null;
        }
        return false;
    }

    // ------------------------------------------------------------------ spell-fix worker: one running, one pending

    private sealed record FixRequest(string Typed, IntPtr Layout, string Alt, IntPtr Other, IntPtr Hwnd, IntPtr Focus,
        int BoundaryVk, HeldBoundary? Held, string Boundary, int Epoch, int Ctx, string Scope);

    private FixRequest? _pendingFix;
    private readonly SemaphoreSlim _fixSignal = new(0);
    private Thread? _fixThread;

    /// <summary>A newer request replaces an older one still waiting: its word is no longer the last one on screen anyway.</summary>
    private void SubmitFix(FixRequest r)
    {
        var replaced = Interlocked.Exchange(ref _pendingFix, r);
        if (replaced?.Held != null && TakeHeld(replaced.Held)) Injector.PressKey(replaced.Held.Vk); // don't swallow its Enter/Tab
        if (_fixThread == null)
        {
            _fixThread = new Thread(FixWorker) { IsBackground = true, Name = "Switcher spell-fix" };
            _fixThread.Start();
        }
        _fixSignal.Release();
    }

    private void FixWorker()
    {
        while (!_disposed)
        {
            _fixSignal.Wait();
            var r = Interlocked.Exchange(ref _pendingFix, null);
            if (r == null) continue;
            if (Volatile.Read(ref _epoch) != r.Epoch)
            {
                if (Debug) Log.Write($"fix '{r.Typed}' stale before start");
                DeliverHeldLater(r.Held);
                continue;
            }
            Decision fix;
            try
            {
                RuleScope.Current = r.Scope;
                fix = _speller.FixEither(r.Typed, r.Layout, r.Alt, r.Other, _settings.AutoSwitchLayout, r.Ctx);
            }
            catch (Exception ex)
            {
                Log.Write("spell fix failed: " + ex);
                DeliverHeldLater(r.Held); // a failed fix must not eat the user's Enter
                continue;
            }
            RunInHook(() => ApplyFix(fix, r));
        }
    }

    /// <summary>Runs inside a hook callback: every earlier key has been seen, and our SendInput is queued atomically.</summary>
    private void ApplyFix(Decision fix, FixRequest r)
    {
        if (_disposed) return;
        var held = r.Held;
        // If the held Enter/Tab is gone, a later key or click has already delivered it in its place: the word is not
        // the last thing before the caret any more (and after Enter the message may be sent).
        if (held != null && !TakeHeld(held)) { if (Debug) Log.Write("fix dropped: held key already delivered"); return; }
        void DeliverHeld() { if (held != null) Injector.PressKey(held.Vk); }

        // The world may have changed while we were thinking. If the user switched windows or fields, nothing is
        // typed anywhere — a held Enter/Tab is dropped rather than delivered to whatever has focus now. Otherwise
        // (feature turned off, app excluded, password field) the word stays as it is and the held key goes through.
        if (Native.GetForegroundWindow() != r.Hwnd || Injector.FocusWindow(r.Hwnd) != r.Focus) { Log.Write("fix dropped: focus moved"); return; }
        if (!_settings.Enabled || !_settings.AutoFixSpelling) { if (Debug) Log.Write("fix dropped: disabled"); DeliverHeld(); return; }
        if (!CanRewrite(r.Hwnd)) { if (Debug) Log.Write("fix dropped: window may not be rewritten"); DeliverHeld(); return; }
        if (_passwords.IsPasswordField(r.Hwnd)) { if (Debug) Log.Write("fix dropped: password field"); DeliverHeld(); return; }

        bool fixing = fix.Kind == ActionKind.FixSpelling && fix.NewText != r.Typed;
        bool hold = held != null;

        // Meanwhile the user may have typed the first letters of the next word: fine, we erase and retype
        // them too (in the new layout if we switch). Anything else — another word finished, a click,
        // an arrow key, a different window — means we no longer know what is on screen: skip the fix.
        var newLayout = fixing && fix.SwitchLayout ? r.Other : r.Layout;
        IReadOnlyList<TypedKey> pending = Array.Empty<TypedKey>();
        IntPtr pendingLayout = IntPtr.Zero;
        bool known = Volatile.Read(ref _epoch) == r.Epoch && _word.TryPending(r.Hwnd, out pending, out pendingLayout);
        if (Debug) Log.Write($"fix '{r.Typed}' → {fix.Kind} '{fix.NewText}' known={known} epoch={r.Epoch}/{_epoch}");
        if (!known)
        {
            DeliverHeld(); // best effort: at least deliver the held Enter/Tab
            return;
        }
        if (pendingLayout == IntPtr.Zero) pendingLayout = r.Layout;
        string pendingOld = WordTracker.Render(pending, pendingLayout);
        string pendingNew = fixing && fix.SwitchLayout ? WordTracker.Render(pending, r.Other) : pendingOld;

        int backspaces; string text;
        if (fixing)
        {
            backspaces = r.Typed.Length + (hold ? 0 : 1) + pendingOld.Length;
            text = fix.NewText + r.Boundary + pendingNew;
        }
        else if (hold) { backspaces = pendingOld.Length; text = r.Boundary + pendingOld; } // held Enter/Tab goes before the new letters
        else { backspaces = 0; text = ""; }

        if (fixing && fix.SwitchLayout) { SwitchLayoutVerified(r.Hwnd, r.Other); _word.SetLayout(r.Other); }
        if (backspaces > 0 || text.Length > 0)
        {
            var swi = System.Diagnostics.Stopwatch.StartNew();
            Injector.Replace(backspaces, text);
            if (Debug) Log.Write($"  Replace({backspaces}, '{text}') took {swi.ElapsedMilliseconds} ms on thread {Environment.CurrentManagedThreadId}");
        }

        if (!fixing)
        {
            Remember(r.Typed, r.Layout, r.Alt, r.Other, r.BoundaryVk, r.Hwnd, wasAuto: false);
            return;
        }
        SetContext(r.Hwnd, Native.LangId(newLayout));
        // an undo must block the pair the corrector actually chose: for a fix in the other layout, that layout's form
        string? rejectFrom = fix.SwitchLayout ? Corrector.StripPunctuation(r.Alt, out _, out _) : null;
        Remember(fix.NewText, newLayout, r.Typed, r.Layout, r.BoundaryVk, r.Hwnd, wasAuto: true, rejectFrom);
        ThreadPool.QueueUserWorkItem(_ => SafeRun(() => Report($"{r.Typed} → {fix.NewText}  [{fix.Reason}]")));
    }

    private void Remember(string text, IntPtr layout, string alt, IntPtr altLayout, int trailingVk, IntPtr hwnd, bool wasAuto,
        string? rejectFrom = null)
    {
        // After Enter the word may be gone (chat message sent), after Tab the caret is in another field — nothing to undo.
        LastWord? lw = null;
        if (trailingVk is not (Native.VK_RETURN or Native.VK_TAB))
        {
            var typedCore = Corrector.StripPunctuation(wasAuto ? alt : text, out _, out _);
            lw = new LastWord(text, layout, alt, altLayout, trailingVk, hwnd, DateTime.UtcNow, wasAuto, typedCore, rejectFrom ?? typedCore);
        }
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
            // the user disagreed with us: never offer this particular replacement again; after several rejections
            // of the same word, the word itself becomes a personal word (file I/O off the hook thread)
            var from = last.RejectFrom;
            var word = last.TypedCore;
            var to = Corrector.StripPunctuation(last.Text, out _, out _);
            var scope = ProcessName(hwnd);
            ThreadPool.QueueUserWorkItem(_ => SafeRun(() =>
            {
                RuleScope.Current = scope; // pool threads keep whatever scope their last job left behind
                int n = _exceptions.Reject(from, to, word);
                Report($"[undo] {last.Text} → {last.AltText}; замена '{from}' → '{to}' больше не предлагается");
                bool ask;
                lock (_lock) ask = n >= Rules.UndosToSuggest && !_exceptions.Contains(word) && _suggested.Add(word);
                if (ask) SuggestWord?.Invoke(word); // the tray asks the user; nothing is learned silently
            }));
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

    /// <summary>Process name (without .exe) of a window, cached per pid.</summary>
    public string ProcessName(IntPtr hwnd) => Processes.Of(hwnd).Name;

    /// <summary>
    /// May we erase and retype text in this window? Not in an excluded program, not in a window class that is a
    /// game or a list rather than a text field (letters there are hotkeys: in a viewport Backspace deletes geometry,
    /// in Explorer it goes back), and not in a program running above our integrity level (UIPI drops our input).
    /// </summary>
    private bool CanRewrite(IntPtr hwnd)
    {
        var proc = Processes.Of(hwnd);
        if (proc.Elevated) { LogSkipOnce("elevated", proc.Name); return false; }
        if (IgnoreExclusions) return true;
        foreach (var ex in _settings.ExcludedProcesses)
            if (string.Equals(ex, proc.Name, StringComparison.OrdinalIgnoreCase)) return false;
        var classes = _settings.ExcludedWindowClasses;
        if (classes.Count > 0)
        {
            string top = Processes.ClassName(hwnd);
            var focus = Injector.FocusWindow(hwnd);
            string inner = focus == hwnd ? top : Processes.ClassName(focus);
            foreach (var c in classes)
                if (string.Equals(c, top, StringComparison.OrdinalIgnoreCase) || string.Equals(c, inner, StringComparison.OrdinalIgnoreCase))
                {
                    LogSkipOnce("window class " + c, proc.Name);
                    return false;
                }
        }
        return true;
    }

    private readonly HashSet<string> _skipsLogged = new();
    /// <summary>Explain once per program why nothing happens there — the log is where the user looks.</summary>
    private void LogSkipOnce(string why, string process)
    {
        lock (_skipsLogged)
            if (_skipsLogged.Count >= 200 || !_skipsLogged.Add(why + "|" + process)) return;
        Log.Write($"not rewriting in '{process}': {why}");
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

    public void Dispose()
    {
        _disposed = true;
        while (_deferred.TryDequeue(out _)) { } // a fix computed for a program that is closing must not fire
        _pendingFix = null;
        _fixSignal.Release();
        _hook.Dispose();
    }
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
        if (TryParse(text, out var h, out var problem)) return h;
        if (!string.IsNullOrWhiteSpace(text)) Log.Write($"Hotkey '{text}': {problem}, using Pause");
        return new Hotkey { Vk = Native.VK_PAUSE };
    }

    /// <summary>A hotkey that parses and is safe to swallow everywhere (see <see cref="IsAllowed"/>).</summary>
    public static bool IsValid(string text) => TryParse(text, out _, out _);

    private static bool TryParse(string text, out Hotkey hotkey, out string problem)
    {
        hotkey = null!; problem = "empty";
        if (string.IsNullOrWhiteSpace(text)) return false;
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
                    if (!Enum.TryParse<Keys>(raw, ignoreCase: true, out var k)) { problem = $"unknown key '{raw}'"; return false; }
                    vk = (uint)k;
                    break;
            }
        }
        if (vk == 0) { problem = "no key"; return false; }
        if (!IsAllowed((Keys)vk, ctrl, alt, win)) { problem = "it would swallow a typing key"; return false; }
        hotkey = new Hotkey { Vk = vk, Ctrl = ctrl, Shift = shift, Alt = alt, Win = win };
        return true;
    }

    /// <summary>
    /// The hotkey is swallowed everywhere, so on its own (or with just Shift) it must not be a key used for typing
    /// or editing: letters, digits, Space, Enter, Backspace, arrows… With Ctrl, Alt or Win anything but a bare
    /// modifier will do.
    /// </summary>
    public static bool IsAllowed(Keys key, bool ctrl, bool alt, bool win)
    {
        if (key is Keys.None or Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu) return false;
        if (ctrl || alt || win) return true;
        return key is Keys.Pause or Keys.Scroll or Keys.Apps or Keys.Insert || (key >= Keys.F1 && key <= Keys.F24);
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
