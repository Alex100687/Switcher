using System.Text;

namespace Switcher;

public readonly record struct TypedKey(uint Vk, uint Scan, bool Shift, bool Caps);

/// <summary>Buffer of physical keys of the word currently being typed. Rendering to text is done per layout.</summary>
public sealed class WordTracker
{
    // written by the hook (UI) thread, read by the spell-fix worker — guard everything
    private readonly object _lock = new();
    private readonly List<TypedKey> _keys = new();
    private IntPtr _layout, _hwnd;
    private bool _hasDigits, _anchored, _forced;
    private (string From, string To)? _reopened;

    public IntPtr Layout { get { lock (_lock) return _layout; } }
    public IntPtr Hwnd { get { lock (_lock) return _hwnd; } }
    public bool HasDigits { get { lock (_lock) return _hasDigits; } }
    public int Count { get { lock (_lock) return _keys.Count; } }
    public bool IsEmpty => Count == 0;
    /// <summary>The word began right after a boundary we saw (or is a whole word we restored) — not the tail of a longer one.</summary>
    public bool Anchored { get { lock (_lock) return _anchored; } }
    /// <summary>The user chose this word's layout with the hotkey: leave its layout alone.</summary>
    public bool Forced { get { lock (_lock) return _forced; } }
    /// <summary>A word we had corrected, reopened with Backspace: typing it back to what it was means "no".</summary>
    public (string From, string To)? Reopened { get { lock (_lock) return _reopened; } }

    public void Reset() { lock (_lock) ResetLocked(); }

    private void ResetLocked()
    {
        _keys.Clear();
        _hasDigits = false;
        _forced = false;
        _reopened = null;
        _layout = IntPtr.Zero;
        _hwnd = IntPtr.Zero;
    }

    public void Push(uint vk, uint scan, IntPtr layout, IntPtr hwnd, bool anchored = true)
    {
        bool shift = Native.IsDown(Native.VK_SHIFT) || Native.IsDown(Native.VK_LSHIFT) || Native.IsDown(Native.VK_RSHIFT);
        bool caps = Native.IsToggled(Native.VK_CAPITAL);
        lock (_lock)
        {
            if (_keys.Count == 0)
            {
                _layout = layout;
                _hwnd = hwnd;
                _anchored = anchored;
            }
            if (IsDigitKey(vk)) _hasDigits = true;
            _keys.Add(new TypedKey(vk, scan, shift, caps));
            // don't let the buffer grow forever on a pasted-looking stream
            if (_keys.Count > 64) ResetLocked();
        }
    }

    public void Backspace()
    {
        lock (_lock)
        {
            if (_keys.Count > 0) _keys.RemoveAt(_keys.Count - 1);
            if (_keys.Count == 0)
            {
                // erased down to nothing and typed anew: still an edit of the word we had corrected
                var reopened = _reopened;
                ResetLocked();
                _reopened = reopened;
            }
            else _hasDigits = _keys.Any(k => IsDigitKey(k.Vk));
        }
    }

    /// <summary>Put a whole word back into the buffer (it is on screen before the caret again and is being edited).</summary>
    public void Restore(IReadOnlyList<TypedKey> keys, IntPtr layout, IntPtr hwnd, bool anchored, bool forced = false, (string From, string To)? reopened = null)
    {
        lock (_lock)
        {
            ResetLocked();
            if (keys.Count == 0) return;
            _keys.AddRange(keys);
            _layout = layout;
            _hwnd = hwnd;
            _anchored = anchored;
            _forced = forced;
            _reopened = reopened;
            _hasDigits = _keys.Any(k => IsDigitKey(k.Vk));
        }
    }

    /// <summary>The keys that type <paramref name="text"/> in a layout; null if some character is not on it (a space, a dash…).</summary>
    public static List<TypedKey>? KeysFor(string text, IntPtr hkl)
    {
        var keys = new List<TypedKey>(text.Length);
        foreach (var ch in text)
        {
            short r = Native.VkKeyScanExW(ch, hkl);
            if (r == -1 || (r & 0x600) != 0) return null; // not on this layout, or needs Ctrl/Alt
            uint vk = (uint)(r & 0xFF);
            if (!IsWordKey(vk)) return null;
            keys.Add(new TypedKey(vk, Native.MapVirtualKeyEx(vk, 0, hkl), (r & 0x100) != 0, false));
        }
        return keys;
    }

    /// <summary>After we switched the window's layout, the keys typed since belong to the new layout.</summary>
    public void SetLayout(IntPtr layout) { lock (_lock) if (_keys.Count > 0) _layout = layout; }

    /// <summary>Atomically: the keys typed so far in this window (empty if the user moved to another window).</summary>
    public bool TryPending(IntPtr hwnd, out IReadOnlyList<TypedKey> keys, out IntPtr layout)
    {
        lock (_lock)
        {
            keys = _keys.ToArray();
            layout = _layout;
            return _keys.Count == 0 || _hwnd == hwnd;
        }
    }

    public static bool IsDigitKey(uint vk) => (vk >= '0' && vk <= '9') || (vk >= Native.VK_NUMPAD0 && vk <= Native.VK_NUMPAD9);

    public static bool IsLetterKey(uint vk) => vk >= 'A' && vk <= 'Z';

    /// <summary>Keys that can be part of a word: letters, digits and the OEM punctuation keys (which are letters in RU).</summary>
    public static bool IsWordKey(uint vk) =>
        IsLetterKey(vk) || IsDigitKey(vk) ||
        (vk >= Native.VK_OEM_1 && vk <= Native.VK_OEM_3) ||
        (vk >= Native.VK_OEM_4 && vk <= Native.VK_OEM_8) ||
        vk == Native.VK_OEM_102;

    /// <summary>Render the buffered keys as text in the given keyboard layout.</summary>
    public string Render(IntPtr hkl) => Render(_keys, hkl);

    public static string Render(IReadOnlyList<TypedKey> keys, IntPtr hkl)
    {
        var sb = new StringBuilder(keys.Count);
        var state = new byte[256];
        var buf = new StringBuilder(8);
        foreach (var k in keys)
        {
            Array.Clear(state);
            if (k.Shift) { state[Native.VK_SHIFT] = 0x80; state[Native.VK_LSHIFT] = 0x80; }
            if (k.Caps) state[Native.VK_CAPITAL] = 0x01;
            buf.Clear();
            uint scan = k.Scan != 0 ? k.Scan : Native.MapVirtualKeyEx(k.Vk, 0, hkl); // synthetic input may carry scan 0
            // flag 1<<2: do not change keyboard state (dead keys) — Windows 10 1607+
            int n = Native.ToUnicodeEx(k.Vk, scan, state, buf, buf.Capacity, 1u << 2, hkl);
            if (n > 0) sb.Append(buf.ToString(0, n));
            else if (n < 0) sb.Append(buf.ToString(0, 1)); // dead key: take the base char
            else return ""; // no translation in this layout → not a word here
        }
        return sb.ToString();
    }

    public IReadOnlyList<TypedKey> Snapshot() { lock (_lock) return _keys.ToArray(); }
}
