namespace LayoutFix;

public enum ActionKind { None, SwitchLayout, FixSpelling }

public sealed record Decision(ActionKind Kind, string NewText, string Reason)
{
    public static readonly Decision Keep = new(ActionKind.None, "", "");
}

/// <summary>Pure decision logic: given a word as typed and its rendering in the other layout, decide what to do.</summary>
public sealed class Corrector
{
    private readonly Dictionaries _dicts;
    private readonly Exceptions _exceptions;
    private readonly Settings _settings;

    public Corrector(Dictionaries dicts, Exceptions exceptions, Settings settings)
    {
        _dicts = dicts;
        _exceptions = exceptions;
        _settings = settings;
    }

    /// <summary>
    /// Fast part (dictionary lookups only) — safe to call from the keyboard hook.
    /// Returns SwitchLayout / Keep, or FixSpelling with empty NewText meaning "unknown word, run <see cref="SuggestFix"/> asynchronously".
    /// </summary>
    public Decision Decide(string typed, int typedLang, string alt, int altLang, bool hasDigits)
    {
        if (!_dicts.IsLoaded || hasDigits) return Decision.Keep;
        if (!_dicts.Supports(typedLang) || !_dicts.Supports(altLang)) return Decision.Keep;

        var core = StripPunctuation(typed, out _, out _);
        var altCore = StripPunctuation(alt, out _, out _);

        if (core.Length == 0) return Decision.Keep;
        if (_exceptions.Contains(core)) return Decision.Keep; // known word (whitelist / user's exceptions)
        if (IsAllUpper(core)) return Decision.Keep; // abbreviations

        bool coreIsWord = IsWordShaped(core);
        if (coreIsWord && _dicts.Check(typedLang, core)) return Decision.Keep;

        // Typed in wrong layout?
        // English dictionary is full of 2-letter abbreviations ("nu", "dr"), so demand one letter more for EN.
        int minLen = altLang == Dictionaries.LangEn ? Math.Max(_settings.MinWordLength, 3) : _settings.MinWordLength;
        // Letters must stay letters: "ютуб" → ".ne," loses two letters to punctuation — not a real conversion.
        bool keepsLetters = alt.Length - altCore.Length <= typed.Length - core.Length;
        if (_settings.AutoSwitchLayout && altCore.Length >= minLen && IsWordShaped(altCore) && keepsLetters
            && (_exceptions.Contains(altCore) || _dicts.Check(altLang, altCore)))
        {
            return new Decision(ActionKind.SwitchLayout, alt, $"'{core}' not in {LangName(typedLang)}, '{altCore}' in {LangName(altLang)}");
        }

        // Unknown in both — candidate for a typo fix (needs Suggest, which is slow → async).
        if (_settings.AutoFixSpelling && coreIsWord && core.Length >= _settings.MinSpellFixLength && core.Length <= 16
            && IsPureLetters(core))
        {
            return new Decision(ActionKind.FixSpelling, "", "unknown word");
        }

        return Decision.Keep;
    }

    /// <summary>Slow part: ask Hunspell for suggestions and accept only an unambiguous, mechanically plausible slip.</summary>
    public Decision SuggestFix(string typed, IntPtr hkl)
    {
        int lang = Native.LangId(hkl);
        var core = StripPunctuation(typed, out var prefix, out var suffix);
        var lower = core.ToLowerInvariant();

        string? best = null;
        int count = 0;
        foreach (var s in _dicts.Suggest(lang, lower))
        {
            if (s.Length == 0 || s.Contains(' ') || s.Contains('-')) continue;
            var sl = s.ToLowerInvariant();
            if (sl == lower) continue;
            if (Normalize(sl) == Normalize(lower)) return Decision.Keep; // differs only by ё → not a typo
            if (!TypoModel.IsPlausible(Normalize(lower), Normalize(sl), hkl)) continue;
            count++;
            best ??= s;
            if (count > 1) break;
        }
        if (count != 1 || best == null) return Decision.Keep;

        var fixedCore = MatchCase(core, best);
        return new Decision(ActionKind.FixSpelling, prefix + fixedCore + suffix, $"'{core}' → '{fixedCore}' ({LangName(lang)})");
    }

    private static string Normalize(string s) => s.Replace('ё', 'е');

    public static string LangName(int lang) => lang switch { 0x0419 => "ru", 0x0409 => "en", _ => lang.ToString("X4") };

    /// <summary>Letters, optionally with inner hyphen/apostrophe.</summary>
    public static bool IsWordShaped(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) || !char.IsLetter(s[^1])) return false;
        foreach (var c in s)
            if (!char.IsLetter(c) && c != '-' && c != '\'' && c != '’') return false;
        return true;
    }

    public static bool IsPureLetters(string s)
    {
        foreach (var c in s) if (!char.IsLetter(c)) return false;
        return s.Length > 0;
    }

    private static bool IsAllUpper(string s)
    {
        int letters = 0;
        foreach (var c in s)
        {
            if (!char.IsLetter(c)) continue;
            if (!char.IsUpper(c)) return false;
            letters++;
        }
        return letters >= 2;
    }

    /// <summary>Strip leading/trailing punctuation and symbols (quotes, brackets, .,!? etc.).</summary>
    public static string StripPunctuation(string s, out string prefix, out string suffix)
    {
        int start = 0, end = s.Length;
        while (start < end && !char.IsLetterOrDigit(s[start])) start++;
        while (end > start && !char.IsLetterOrDigit(s[end - 1])) end--;
        prefix = s[..start];
        suffix = s[end..];
        return s[start..end];
    }

    /// <summary>Apply the casing pattern of <paramref name="original"/> to <paramref name="replacement"/>.</summary>
    public static string MatchCase(string original, string replacement)
    {
        if (original.Length == 0 || replacement.Length == 0) return replacement;
        bool firstUpper = char.IsUpper(original[0]);
        bool allUpper = original.Length > 1 && original.All(c => !char.IsLetter(c) || char.IsUpper(c));
        if (allUpper) return replacement.ToUpperInvariant();
        if (firstUpper) return char.ToUpperInvariant(replacement[0]) + replacement[1..].ToLowerInvariant();
        return replacement.ToLowerInvariant();
    }

    /// <summary>Optimal string alignment distance (Levenshtein + adjacent transposition).</summary>
    public static int DamerauLevenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (Math.Abs(n - m) > 2) return 3; // early out, we only care about ≤ 1
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[n, m];
    }
}
