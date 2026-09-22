namespace Switcher;

public enum ActionKind { None, SwitchLayout, FixSpelling }

/// <param name="Score">Lower is better; used to compare candidate fixes.</param>
/// <param name="SwitchLayout">The fix is in the other layout: switch it and type the fixed word.</param>
public sealed record Decision(ActionKind Kind, string NewText, string Reason, double Score = 0, bool SwitchLayout = false)
{
    public static readonly Decision Keep = new(ActionKind.None, "", "");
}

/// <summary>Pure decision logic: given a word as typed and its rendering in the other layout, decide what to do.</summary>
public sealed class Corrector
{
    private readonly Dictionaries _dicts;
    private readonly Rules _exceptions;
    private readonly Settings _settings;
    private readonly Frequencies _freq;

    public Corrector(Dictionaries dicts, Rules rules, Settings settings, Frequencies freq)
    {
        _dicts = dicts;
        _exceptions = rules;
        _settings = settings;
        _freq = freq;
    }

    /// <summary>Word-shaped and known — used to update the language context.</summary>
    public bool IsRealWord(int lang, string text)
    {
        var core = StripPunctuation(text, out _, out _);
        return core.Length >= 2 && IsWordShaped(core) && IsKnown(lang, core);
    }

    /// <summary>A real word of the language: in the dictionary, in the whitelist/exceptions, or frequent enough in speech (чо, щас).</summary>
    public bool IsKnown(int lang, string word) =>
        _exceptions.Contains(word) || _dicts.Check(lang, word) || IsFrequent(lang, word);

    /// <summary>Colloquial word by frequency alone (чо, щас). Two-letter tokens in the lists are noisy ("lf", "bp"): Russian top-5000 only.</summary>
    private bool IsFrequent(int lang, string word)
    {
        int rank = _freq.Rank(lang, word);
        if (word.Length >= 3) return rank <= SpellFixer.KnownRankLimit(lang);
        return lang == Dictionaries.LangRu && rank <= 5_000;
    }

    /// <summary>
    /// Fast part (dictionary lookups only) — safe to call from the keyboard hook.
    /// Returns SwitchLayout / Keep, or FixSpelling with empty NewText meaning "unknown word, run <see cref="SuggestFix"/> asynchronously".
    /// </summary>
    /// <param name="contextLang">Language of the words typed just before in this window (0 = unknown).</param>
    public Decision Decide(string typed, int typedLang, string alt, int altLang, bool hasDigits, int contextLang = 0)
    {
        if (!_dicts.IsLoaded || hasDigits) return Decision.Keep;
        if (!_dicts.Supports(typedLang) || !_dicts.Supports(altLang)) return Decision.Keep;

        var core = StripPunctuation(typed, out _, out _);
        var altCore = StripPunctuation(alt, out _, out _);

        if (core.Length == 0) return Decision.Keep;
        // explicit autocorrect rules win over everything, including dictionary words ("ихний" → "их")
        if (_settings.AutoFixSpelling && _exceptions.TryAutocorrect(core, out _))
            return new Decision(ActionKind.FixSpelling, "", "autocorrect");
        if (_exceptions.Contains(core)) return Decision.Keep; // known word (whitelist / user's exceptions)
        if (IsCamelCase(core) || IsCamelCase(altCore)) return Decision.Keep; // myVar, GameObject — code, not prose
        bool allUpper = IsAllUpper(core); // abbreviation (API) — or Caps Lock in the wrong layout (GHBDTN)

        // English dictionary is full of 2-letter abbreviations ("nu", "dr"), so demand one letter more for EN.
        int minLen = altLang == Dictionaries.LangEn ? Math.Max(_settings.MinWordLength, 3) : _settings.MinWordLength;
        // Letters must stay letters: "ютуб" → ".ne," loses two letters to punctuation — not a real conversion.
        bool keepsLetters = alt.Length - altCore.Length <= typed.Length - core.Length;
        bool altIsWord = _settings.AutoSwitchLayout && altCore.Length >= minLen && IsWordShaped(altCore) && keepsLetters
                         && IsKnown(altLang, altCore) && !_exceptions.IsBlocked(core, altCore);

        bool coreIsWord = IsWordShaped(core);
        if (coreIsWord && IsKnown(typedLang, core))
        {
            // A word in both languages ("tot" = "еще", "ult" = "где", "руку" = "here"): the surrounding text decides,
            // and with no context a hugely more common word in the other language wins.
            if (altIsWord && !_exceptions.Contains(core) && PreferOther(core, typedLang, altCore, altLang, contextLang, out var why))
                return new Decision(ActionKind.SwitchLayout, alt, $"'{core}' ({LangName(typedLang)}) vs '{altCore}' ({LangName(altLang)}): {why}");
            return Decision.Keep;
        }

        // Typed in wrong layout?
        if (altIsWord)
            return new Decision(ActionKind.SwitchLayout, alt, $"'{core}' not in {LangName(typedLang)}, '{altCore}' in {LangName(altLang)}");

        if (allUpper) return Decision.Keep; // don't "fix" abbreviations

        // Unknown in both — candidate for a typo fix in either layout (needs Suggest, which is slow → async).
        bool typedFixable = IsPureLetters(core) && core.Length >= _settings.MinSpellFixLength && core.Length <= 20;
        bool altFixable = _settings.AutoSwitchLayout && keepsLetters && IsPureLetters(altCore)
                          && altCore.Length >= Math.Max(_settings.MinSpellFixLength, minLen) && altCore.Length <= 20;
        if (_settings.AutoFixSpelling && (typedFixable || altFixable))
            return new Decision(ActionKind.FixSpelling, "", "unknown word");

        return Decision.Keep;
    }

    public bool PreferOther(string core, int typedLang, string altCore, int altLang, int contextLang, out string why)
    {
        if (contextLang == altLang) { why = "context"; return true; }
        if (contextLang == typedLang) { why = ""; return false; }
        int typedRank = _freq.Rank(typedLang, core);
        int altRank = _freq.Rank(altLang, altCore);
        // no context: switch only to a very common word from a rare/unknown one (еще:67 vs tot:23264, but not рук:1624 vs her:60)
        if (altRank <= 300 && (typedRank == int.MaxValue || (typedRank > 3000 && typedRank >= 20L * altRank)))
        {
            why = $"rank {altRank} vs {(typedRank == int.MaxValue ? "-" : typedRank)}";
            return true;
        }
        why = "";
        return false;
    }

    /// <summary>An uppercase letter after the first one: an identifier (camelCase, PascalCase), not a word.</summary>
    public static bool IsCamelCase(string s)
    {
        if (s.Length < 2 || IsAllUpper(s)) return false;
        for (int i = 1; i < s.Length; i++) if (char.IsUpper(s[i])) return true;
        return false;
    }

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

    public static bool IsAllUpper(string s)
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
}
