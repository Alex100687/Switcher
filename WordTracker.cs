using System.Text;

namespace LayoutFix;

public readonly record struct TypedKey(uint Vk, uint Scan, bool Shift, bool Caps);

/// <summary>Buffer of physical keys of the word currently being typed. Rendering to text is done per layout.</summary>
public sealed class WordTracker
{
    private readonly List<TypedKey> _keys = new();

    public IntPtr Layout { get; private set; }
    public IntPtr Hwnd { get; private set; }
    public bool HasDigits { get; private set; }
    public int Count => _keys.Count;
    public bool IsEmpty => _keys.Count == 0;

    public void Reset()
    {
        _keys.Clear();
        HasDigits = false;
        Layout = IntPtr.Zero;
        Hwnd = IntPtr.Zero;
    }

    public void Push(uint vk, uint scan, IntPtr layout, IntPtr hwnd)
    {
        if (_keys.Count == 0)
        {
            Layout = layout;
            Hwnd = hwnd;
        }
        if (IsDigitKey(vk)) HasDigits = true;
        bool shift = Native.IsDown(Native.VK_SHIFT) || Native.IsDown(Native.VK_LSHIFT) || Native.IsDown(Native.VK_RSHIFT);
        bool caps = Native.IsToggled(Native.VK_CAPITAL);
        _keys.Add(new TypedKey(vk, scan, shift, caps));
        // don't let the buffer grow forever on a pasted-looking stream
        if (_keys.Count > 64) Reset();
    }

    public void Backspace()
    {
        if (_keys.Count > 0) _keys.RemoveAt(_keys.Count - 1);
        if (_keys.Count == 0) Reset();
        else HasDigits = _keys.Any(k => IsDigitKey(k.Vk));
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

    public IReadOnlyList<TypedKey> Snapshot() => _keys.ToArray();
}
