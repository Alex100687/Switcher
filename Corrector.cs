namespace Switcher;

/// <summary>
/// None — leave the word; SwitchLayout — retype in the other layout (synchronously); FixSpelling — unknown word, the
/// fix is computed asynchronously (NewText empty) or already known (the worker's result); Replace — retype in the same
/// layout right away (letter case, spaces, punctuation).
/// </summary>
public enum ActionKind { None, SwitchLayout, FixSpelling, Replace }

/// <param name="Score">Lower is better; used to compare candidate fixes.</param>
/// <param name="SwitchLayout">The fix is in the other layout: switch it and type the fixed word.</param>
public sealed record Decision(ActionKind Kind, string NewText, string Reason, double Score = 0, bool SwitchLayout = false)
{
    /// <summary>Characters before the current word that <see cref="NewText"/> replaces too: previous words and the spaces after them.</summary>
    public int ErasePrevious { get; init; }
    /// <summary>What those characters were (the undo types them back).</summary>
    public string PreviousText { get; init; } = "";
    /// <summary>How many previous words <see cref="NewText"/> took over.</summary>
    public int PreviousWords { get; init; }
    /// <summary>The word was typed with Caps Lock on by mistake ("пРИВЕТ"): turn Caps Lock off after retyping it.</summary>
    public bool CapsOff { get; init; }
    /// <summary>Undoing it teaches nothing: a capital at the start of a sentence is about the place, not the word.</summary>
    public bool Learn { get; init; } = true;
    /// <summary>The spelling fixer's edit cost (without the rarity penalty of <see cref="Score"/>).</summary>
    public double Cost { get; init; }

    public static readonly Decision Keep = new(ActionKind.None, "", "");
}

/// <summary>The word before the current one, exactly as it is on screen and followed by exactly one space.</summary>
/// <param name="Lang">Language of <paramref name="Text"/>.</param>
/// <param name="AltText">For a word left as typed: the same keys in the other layout. For a word we changed: what was typed.</param>
/// <param name="StartedSentence">The word began a sentence (it should keep its capital if we retype it).</param>
public sealed record PrevWord(string Text, int Lang, string AltText, int AltLang, bool WasAuto, bool StartedSentence, PrevWord? Before);

/// <summary>What surrounds the word being decided.</summary>
/// <param name="SentenceStart">The previous word ended a sentence (". ", "! ", "? ").</param>
/// <param name="Prev">The word right before it, if we know what is on screen there.</param>
/// <param name="Fragment">The word began where we saw no word boundary (the caret was moved into text): it may be the tail of a longer word.</param>
public sealed record WordContext(bool SentenceStart = false, PrevWord? Prev = null, bool Fragment = false)
{
    public static readonly WordContext None = new();
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
    /// Returns SwitchLayout / Replace / Keep, or FixSpelling with empty NewText meaning "unknown word, fix it asynchronously".
    /// </summary>
    /// <param name="contextLang">Language of the words typed just before in this window (0 = unknown).</param>
    public Decision Decide(string typed, int typedLang, string alt, int altLang, bool hasDigits, int contextLang = 0, WordContext? context = null)
    {
        var wc = context ?? WordContext.None;
        if (!_dicts.IsLoaded) return Decision.Keep;
        // digit keys with Shift are punctuation ("!" is Shift+1, the Russian "?" is Shift+7): only real digits count
        hasDigits &= typed.Any(char.IsDigit) || alt.Any(char.IsDigit);
        if (!_dicts.Supports(typedLang) || !_dicts.Supports(altLang)) return Decision.Keep;

        var single = DecideWord(typed, typedLang, alt, altLang, hasDigits, contextLang, wc.SentenceStart && !wc.Fragment);
        // The tail of a longer word: letters typed in the wrong layout are wrong wherever they are, but spelling,
        // capitals and spaces can only be judged on the whole word.
        if (wc.Fragment) return single.Kind == ActionKind.SwitchLayout ? single with { NewText = alt, CapsOff = false } : Decision.Keep;
        if (wc.Prev == null || hasDigits) return single;
        return DecidePair(single, typed, typedLang, alt, altLang, wc) ?? single;
    }

    private Decision DecideWord(string typed, int typedLang, string alt, int altLang, bool hasDigits, int contextLang, bool sentenceStart)
    {
        if (hasDigits) return Decision.Keep;
        var core = StripPunctuation(typed, out var pre, out var suf);
        var altCore = StripPunctuation(alt, out var altPre, out var altSuf);

        if (core.Length == 0) return Decision.Keep;
        // explicit autocorrect rules win over everything, including dictionary words ("ихний" → "их")
        if (_settings.AutoFixSpelling && _exceptions.TryAutocorrect(core, out _))
            return new Decision(ActionKind.FixSpelling, "", "autocorrect");
        if (_exceptions.Contains(core)) return Decision.Keep; // known word (whitelist / user's exceptions)

        // "привет,как" → "привет, как" (and "ghbdtn?rfr", the same keys in the wrong layout)
        if (_settings.FixSpaces && SpaceAfterPunctuation(typed, core, typedLang, alt, altCore, altLang, sentenceStart) is { } spaced)
            return spaced;

        // Shift held a moment too long ("ПОжалуйста") or Caps Lock on by mistake ("пРИВЕТ"): the word is "Пожалуйста"
        bool capsTyped = false, capsAlt = false, slipTyped = false, slipAlt = false;
        if (_settings.FixCase)
        {
            var unslipped = UnslipCase(core, typedLang, out capsTyped);
            var altUnslipped = UnslipCase(altCore, altLang, out capsAlt);
            slipTyped = unslipped != core; slipAlt = altUnslipped != altCore;
            core = unslipped; altCore = altUnslipped;
        }
        // A shift slip and a typo at once ("ППривет" — the first key twice, "ПРивте"): the fixer may find the word;
        // AfterFix takes its answer only if it fits a slip, so "ПКшка", "ДРшка" (abbreviation + suffix) stay.
        if (_settings.FixCase && _settings.AutoFixSpelling && !slipTyped && !slipAlt
            && (IsSlipPattern(core) || (_settings.AutoSwitchLayout && IsSlipPattern(altCore)))
            && core.Length <= 20)
            return new Decision(ActionKind.FixSpelling, "", "shift slip, unknown word");
        // myVar, GameObject — code, not prose. (The other layout of a shift slip has the same odd capitals —
        // "ПОжалуйста" is "GJ;fkeqcnf" — which says nothing once one side reads as a word.)
        if ((IsCamelCase(core) && !slipAlt) || (IsCamelCase(altCore) && !slipTyped)) return Decision.Keep;
        bool allUpper = IsAllUpper(core); // abbreviation (API) — or Caps Lock in the wrong layout (GHBDTN)

        // English dictionary is full of 2-letter abbreviations ("nu", "dr"), so demand one letter more for EN.
        int minLen = altLang == Dictionaries.LangEn ? Math.Max(_settings.MinWordLength, 3) : _settings.MinWordLength;
        // Letters must stay letters: "ютуб" → ".ne," loses two letters to punctuation — not a real conversion.
        bool keepsLetters = alt.Length - altCore.Length <= typed.Length - core.Length;
        bool altIsWord = _settings.AutoSwitchLayout && altCore.Length >= minLen && IsWordShaped(altCore) && keepsLetters
                         && IsKnown(altLang, altCore) && !_exceptions.IsBlocked(core, altCore);
        string altText = altPre + altCore + altSuf, typedText = pre + core + suf;

        bool coreIsWord = IsWordShaped(core);
        if (coreIsWord && IsKnown(typedLang, core))
        {
            // A word in both languages ("tot" = "еще", "ult" = "где", "руку" = "here"): the surrounding text decides,
            // and with no context a hugely more common word in the other language wins.
            if (altIsWord && !_exceptions.Contains(core) && PreferOther(core, typedLang, altCore, altLang, contextLang, out var why))
                return Switch(altText, altLang, sentenceStart, capsAlt, $"'{core}' ({LangName(typedLang)}) vs '{altCore}' ({LangName(altLang)}): {why}");
            return KeepOrRecase(typed, typedText, typedLang, sentenceStart, capsTyped);
        }

        // Typed in wrong layout?
        if (altIsWord)
            return Switch(altText, altLang, sentenceStart, capsAlt, $"'{core}' not in {LangName(typedLang)}, '{altCore}' in {LangName(altLang)}");

        if (allUpper) return Decision.Keep; // don't "fix" abbreviations

        // Unknown in both — candidate for a typo fix in either layout (needs Suggest, which is slow → async).
        bool typedFixable = IsPureLetters(core) && core.Length >= _settings.MinSpellFixLength && core.Length <= 20;
        bool altFixable = _settings.AutoSwitchLayout && keepsLetters && IsPureLetters(altCore)
                          && altCore.Length >= Math.Max(_settings.MinSpellFixLength, minLen) && altCore.Length <= 20;
        if (_settings.AutoFixSpelling && (typedFixable || altFixable))
            return new Decision(ActionKind.FixSpelling, "", "unknown word");

        return KeepOrRecase(typed, typedText, typedLang, sentenceStart, capsTyped); // an unknown name at the start of a sentence still gets its capital
    }

    private Decision Switch(string text, int lang, bool sentenceStart, bool capsOff, string reason) =>
        new(ActionKind.SwitchLayout, Recase(text, lang, sentenceStart), reason) { CapsOff = capsOff };

    /// <summary>The word stays — unless only its letter case needs fixing.</summary>
    private Decision KeepOrRecase(string typed, string text, int lang, bool sentenceStart, bool capsOff)
    {
        var cased = Recase(text, lang, sentenceStart);
        if (cased == typed) return Decision.Keep;
        bool onlySentence = Recase(text, lang, false) == typed;
        return new Decision(ActionKind.Replace, cased, onlySentence ? "start of sentence" : "letter case") { CapsOff = capsOff, Learn = !onlySentence };
    }

    /// <summary>
    /// The worker's spelling fix gets the same letter case treatment as the synchronous decisions; an unknown word
    /// the fixer left alone may still need a capital (a name at the start of a sentence).
    /// </summary>
    public Decision AfterFix(Decision fix, string typed, string alt, int typedLang, int otherLang, bool sentenceStart)
    {
        if (fix.Kind == ActionKind.FixSpelling && fix.NewText.Length > 0)
        {
            // the word as the fixer saw it — in the other layout for a fix with a switch
            var source = StripPunctuation(fix.SwitchLayout ? alt : typed, out _, out _);
            var result = StripPunctuation(fix.NewText, out var pre, out var suf);
            if (IsSlipPattern(source))
            {
                if (!FitsShiftSlip(source, result, fix.Cost)) return AfterFix(Decision.Keep, typed, alt, typedLang, otherLang, sentenceStart);
                // "пРИВТЕ" (Caps Lock + a typo): the fixer copies the small first letter — it is a capital; Caps Lock goes off
                if (IsInvertedCaps(source))
                    fix = fix with { NewText = pre + char.ToUpperInvariant(result[0]) + result[1..].ToLowerInvariant() + suf, CapsOff = true };
            }
            return fix with { NewText = Recase(fix.NewText, fix.SwitchLayout ? otherLang : typedLang, sentenceStart) };
        }
        var cased = Recase(typed, typedLang, sentenceStart);
        if (cased == typed) return fix;
        bool onlySentence = Recase(typed, typedLang, false) == typed;
        return new Decision(ActionKind.FixSpelling, cased, onlySentence ? "start of sentence" : "letter case") { Learn = !onlySentence };
    }

    // ------------------------------------------------------------------ letter case

    /// <summary>
    /// The letter case the word should have: shift slips undone ("ПОжалуйста", "пРИВЕТ"), dictionary capitals for names,
    /// places and abbreviations ("москва" → "Москва", "сша" → "США", "i" → "I"), and a capital at the start of a sentence.
    /// Personal and whitelisted words are left exactly as typed; so are case changes the user has undone before.
    /// </summary>
    public string Recase(string text, int lang, bool sentenceStart)
    {
        var core = StripPunctuation(text, out var pre, out var suf);
        if (core.Length == 0 || _exceptions.Contains(core)) return text;
        bool capitalize = sentenceStart && _settings.CapitalizeSentences;
        if (!IsWordShaped(core))
        {
            // "в общем" (an autocorrect result) at the start of a sentence: only its first letter
            int sp = core.IndexOf(' ');
            if (capitalize && sp > 0 && IsWordShaped(core[..sp]) && IsAllLower(core[..sp]))
                return pre + char.ToUpperInvariant(core[0]) + core[1..] + suf;
            return text;
        }
        string result = core;
        if (_settings.FixCase)
        {
            result = UnslipCase(result, lang, out _);
            if (IsAllLower(result))
            {
                var proper = _dicts.ProperCase(lang, result);
                if (proper != null && !_exceptions.IsBlocked(result, proper)) result = proper;
            }
        }
        if (capitalize && IsAllLower(result) && !_exceptions.IsBlocked(result, char.ToUpperInvariant(result[0]) + result[1..]))
            result = char.ToUpperInvariant(result[0]) + result[1..];
        return pre + result + suf;
    }

    /// <summary>"ПОжалуйста" (Shift held too long) / "пРИВЕТ" (Caps Lock on) → "Пожалуйста" / "Привет" — only if that is a word.</summary>
    private string UnslipCase(string core, int lang, out bool capsLock)
    {
        capsLock = false;
        bool two = IsTwoCaps(core), inverted = IsInvertedCaps(core);
        if (!two && !inverted) return core;
        var title = char.ToUpperInvariant(core[0]) + core[1..].ToLowerInvariant();
        if (!IsKnown(lang, title) || _exceptions.IsBlocked(core, title)) return core;
        capsLock = inverted;
        return title;
    }

    /// <summary>Two capitals, then at least two lowercase letters and nothing else: "ПОжалуйста", "HEllo" (not "IDs", "ВКонтакте" if unknown).</summary>
    public static bool IsTwoCaps(string s)
    {
        if (s.Length < 4 || !IsPureLetters(s) || !char.IsUpper(s[0]) || !char.IsUpper(s[1])) return false;
        for (int i = 2; i < s.Length; i++) if (!char.IsLower(s[i])) return false;
        return true;
    }

    /// <summary>Capitals the way a held Shift or a forgotten Caps Lock leaves them.</summary>
    public static bool IsSlipPattern(string s) => IsTwoCaps(s) || IsInvertedCaps(s);

    /// <summary>
    /// Can a fix of a shift-slip word be explained by the fingers? The first key may have been pressed twice ("ППривет"
    /// → "Привет"); what remains must then be the word itself, its first two letters kept, with at most one cheap slip
    /// after them ("ПРивте" → "Привет", "ППривед" → "Привет"). Not "ПКшка" → "Пушка" (the К is not a letter of
    /// "пушка") nor "ДРшка" → "Драка" (two edits): those are abbreviations with a suffix.
    /// </summary>
    private static bool FitsShiftSlip(string original, string fixedCore, double cost)
    {
        var o = original.ToLowerInvariant();
        var f = fixedCore.ToLowerInvariant();
        if (o.Length >= 3 && o[0] == o[1])
        {
            o = o[1..];                 // the doubled first key
            cost -= EditCost.Cheap;     // what the fixer paid for dropping it
            if (f == o) return true;
        }
        return cost <= 0.75 && f.Length >= 2 && o.Length >= 2 && f[..2] == o[..2];
    }

    /// <summary>First letter small, all the others capital: Caps Lock was on and Shift pressed for the capital ("пРИВЕТ").</summary>
    public static bool IsInvertedCaps(string s)
    {
        if (s.Length < 3 || !IsPureLetters(s) || !char.IsLower(s[0])) return false;
        for (int i = 1; i < s.Length; i++) if (!char.IsUpper(s[i])) return false;
        return true;
    }

    private static bool IsAllLower(string s)
    {
        bool any = false;
        foreach (var c in s)
        {
            if (!char.IsLetter(c)) continue;
            if (!char.IsLower(c)) return false;
            any = true;
        }
        return any;
    }

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "др", "пр", "см", "ср", "стр", "рис", "табл", "гл", "гг", "вв", "ул", "корп", "обл", "руб", "коп", "тыс", "млн",
        "млрд", "им", "проф", "доц", "акад", "св", "тел", "напр", "англ", "рус", "лат", "мин", "сек", "мм", "км", "кг", "шт",
        "etc", "vs", "mr", "mrs", "ms", "dr", "st", "jr", "sr", "inc", "ltd", "co", "no", "vol", "fig", "approx", "eg", "ie", "pp",
    };

    /// <summary>
    /// Does the word end a sentence, so that the next one starts with a capital? "!" and "?" do; "." does unless the
    /// word is an abbreviation ("т.е.", "др.", "г."), an initial, a number; an ellipsis does not (the thought goes on).
    /// </summary>
    public static bool EndsSentence(string text)
    {
        var t = text.TrimEnd('"', '»', '”', '’', '\'', ')', ']');
        if (t.Length == 0 || t.EndsWith("...") || t.EndsWith('…')) return false;
        char last = t[^1];
        if (last is '!' or '?') return true;
        if (last != '.') return false;
        var core = StripPunctuation(t, out _, out _);
        if (core.Length <= 1 || core.Contains('.') || core.Any(char.IsDigit) || Abbreviations.Contains(core)) return false;
        return IsWordShaped(core);
    }

    // ------------------------------------------------------------------ spaces and punctuation

    private const string SpacedPunctuation = ",;:!?";

    /// <summary>"привет,как" → "привет, как"; the same keys in the wrong layout ("ghbdtn?rfr") → switched and spaced.</summary>
    private Decision? SpaceAfterPunctuation(string typed, string core, int typedLang, string alt, string altCore, int altLang, bool sentenceStart)
    {
        StripPunctuation(typed, out var pre, out var suf);
        var own = SpaceSegments(core, typedLang, sentenceStart);
        if (own != null) return new Decision(ActionKind.Replace, pre + own + suf, "space after punctuation");
        // a real word as typed is not a wrong-layout "a;b" ("может" is "vj;tn" in the other layout)
        if (!_settings.AutoSwitchLayout || (IsWordShaped(core) && IsKnown(typedLang, core))) return null;
        StripPunctuation(alt, out var altPre, out var altSuf);
        var other = SpaceSegments(altCore, altLang, sentenceStart);
        if (other != null && !_exceptions.IsBlocked(core, altCore))
            return new Decision(ActionKind.SwitchLayout, altPre + other + altSuf, "wrong layout, space after punctuation");
        return null;
    }

    /// <summary>"a,b" → "a, b" when every part is a known word of 2+ letters; null otherwise (URLs, code, numbers stay).</summary>
    private string? SpaceSegments(string core, int lang, bool sentenceStart)
    {
        var parts = new List<string>();
        var seps = new List<char>();
        int start = 0;
        for (int i = 0; i < core.Length; i++)
        {
            if (SpacedPunctuation.IndexOf(core[i]) < 0) continue;
            parts.Add(core[start..i]);
            seps.Add(core[i]);
            start = i + 1;
        }
        if (seps.Count == 0) return null;
        parts.Add(core[start..]);
        foreach (var p in parts)
            if (p.Length < 2 || !IsWordShaped(p) || !IsKnown(lang, p) || (p.Length == 2 && !IsCommonWord(p, lang))) return null;
        var sb = new System.Text.StringBuilder(Recase(parts[0], lang, sentenceStart));
        for (int i = 0; i < seps.Count; i++)
            sb.Append(seps[i]).Append(' ').Append(Recase(parts[i + 1], lang, seps[i] is '!' or '?'));
        return sb.ToString();
    }

    /// <summary>Decisions that also rewrite the word before: short words in the same wrong layout, punctuation, spaces.</summary>
    private Decision? DecidePair(Decision single, string typed, int typedLang, string alt, int altLang, WordContext wc)
    {
        var prev = wc.Prev!;
        // Short words right before a word typed in the wrong layout were typed in the same wrong layout: "z ljvf" → "я дома"
        if (single.Kind == ActionKind.SwitchLayout && _settings.AutoSwitchLayout)
        {
            var fixedWords = new List<string>();
            string oldText = "";
            int erase = 0;
            for (var p = prev; p != null && fixedWords.Count < 3 && ShortWordInWrongLayout(p, typedLang, altLang, out var fixedWord); p = p.Before)
            {
                fixedWords.Insert(0, fixedWord);
                oldText = p.Text + " " + oldText;
                erase += p.Text.Length + 1;
            }
            if (fixedWords.Count > 0)
                return single with
                {
                    NewText = string.Join(" ", fixedWords) + " " + single.NewText,
                    ErasePrevious = erase, PreviousText = oldText, PreviousWords = fixedWords.Count,
                    Reason = single.Reason + $"; {fixedWords.Count} short word(s) before it too",
                };
        }
        if (!_settings.FixSpaces) return null;
        if (MovePunctuationBack(single, typed, typedLang, altLang, prev) is { } moved) return moved;
        if (single.Kind != ActionKind.SwitchLayout && Respace(typed, typedLang, prev) is { } respaced) return respaced;
        return null;
    }

    private static readonly Dictionary<int, string> SingleLetterWords = new()
    {
        [Dictionaries.LangRu] = "авиксоуя",
        [Dictionaries.LangEn] = "ai",
    };

    private static bool IsSingleLetterWord(string w, int lang) =>
        w.Length == 1 && SingleLetterWords.TryGetValue(lang, out var set) && set.Contains(char.ToLowerInvariant(w[0]));

    /// <summary>A word we left alone that is gibberish here but a short word in the other layout ("z" → "я", "rfr" → "как").</summary>
    private bool ShortWordInWrongLayout(PrevWord p, int typedLang, int altLang, out string fixedWord)
    {
        fixedWord = "";
        if (p.WasAuto || p.Lang != typedLang || p.AltLang != altLang) return false;
        var core = StripPunctuation(p.Text, out _, out _);
        var altCore = StripPunctuation(p.AltText, out _, out _);
        if (core.Length == 0 || core.Length > 3 || altCore.Length != core.Length || !IsWordShaped(altCore)) return false;
        if (p.AltText.Length - altCore.Length > p.Text.Length - core.Length) return false; // letters must stay letters
        if (core.Length == 1)
        {
            if (IsSingleLetterWord(core, typedLang) || !IsSingleLetterWord(altCore, altLang)) return false;
        }
        else if (IsKnown(typedLang, core) || !IsKnown(altLang, altCore)) return false;
        if (_exceptions.IsBlocked(core, altCore)) return false;
        fixedWord = Recase(p.AltText, altLang, p.StartedSentence);
        return true;
    }

    /// <summary>
    /// Punctuation typed after the space instead of before it: "привет ,как" → "привет, как", "привет ," → "привет,".
    /// Not for emoticons (":)", ";D") and not for ".net"-like words.
    /// </summary>
    private Decision? MovePunctuationBack(Decision single, string typed, int typedLang, int altLang, PrevWord prev)
    {
        if (single.Kind == ActionKind.FixSpelling || single.ErasePrevious > 0) return null;
        string text = single.Kind is ActionKind.SwitchLayout or ActionKind.Replace ? single.NewText : typed;
        int lang = single.Kind == ActionKind.SwitchLayout ? altLang : typedLang;
        int n = 0;
        while (n < text.Length && (SpacedPunctuation.IndexOf(text[n]) >= 0 || text[n] == '.')) n++;
        if (n == 0) return null;
        string punct = text[..n], rest = text[n..];
        if (rest.Length > 0)
        {
            var restCore = StripPunctuation(rest, out var restPre, out _);
            if (punct.Contains('.') || restPre.Length > 0 || restCore.Length < 2 || !IsWordShaped(restCore)) return null;
        }
        if (prev.Text.Length == 0 || !char.IsLetterOrDigit(prev.Text[^1])) return null;
        string head = prev.Text + punct;
        string newText = rest.Length > 0 ? head + " " + Recase(rest, lang, EndsSentence(head)) : head;
        if (_exceptions.IsBlocked(prev.Text + " " + typed, newText)) return null;
        return new Decision(single.Kind == ActionKind.SwitchLayout ? ActionKind.SwitchLayout : ActionKind.Replace, newText, "punctuation belongs to the word before")
        {
            ErasePrevious = prev.Text.Length + 1, PreviousText = prev.Text + " ", PreviousWords = 1, CapsOff = single.CapsOff,
        };
    }

    /// <summary>
    /// A space typed one or two letters early or late ("ка кдела" → "как дела", "какд ела" → "как дела") or where there
    /// should be none ("при вет" → "привет"). Only when the pair as typed is not two real words, and the new pair is.
    /// For a previous word we had already corrected, what was typed is tried as well ("какд" fixed to "как", then "ела").
    /// </summary>
    private Decision? Respace(string typed, int lang, PrevWord prev)
    {
        if (prev.Lang != lang) return null;
        var core2 = StripPunctuation(typed, out var pre2, out var suf2);
        var screen1 = StripPunctuation(prev.Text, out var pre1, out var suf1);
        if (pre2.Length > 0 || suf1.Length > 0 || !IsPureLetters(core2) || !IsPureLetters(screen1)) return null;

        var sources = new List<string> { screen1 };
        if (prev.WasAuto && prev.AltLang == prev.Lang)
        {
            var original = StripPunctuation(prev.AltText, out var po, out var so);
            if (po.Length == 0 && so.Length == 0 && IsPureLetters(original) && !original.Equals(screen1, StringComparison.OrdinalIgnoreCase))
                sources.Add(original);
        }
        bool screenPairKnown = IsCommonWord(screen1, lang) && IsCommonWord(core2, lang);
        double screenScore = screenPairKnown ? Rarity(screen1, lang) + Rarity(core2, lang) : double.MaxValue;

        string? bestA = null, bestB = null;
        double best = double.MaxValue;
        foreach (var first in sources)
        {
            if (IsCommonWord(first, lang) && IsCommonWord(core2, lang)) continue; // nothing wrong with this pair
            string joined = first + core2;
            if (joined.Length >= 3 && IsCommonWord(joined, lang))
            {
                double s = Rarity(joined, lang) + 0.3;
                if (s < best) { best = s; bestA = joined; bestB = null; }
            }
            for (int shift = -2; shift <= 2; shift++)
            {
                int at = first.Length + shift;
                if (shift == 0 || at < 1 || at > joined.Length - 1) continue;
                string a = joined[..at], b = joined[at..];
                if (!IsCommonWord(a, lang) || !IsCommonWord(b, lang)) continue;
                double s = Rarity(a, lang) + Rarity(b, lang) + 0.4 * Math.Abs(shift);
                if (s < best) { best = s; bestA = a; bestB = b; }
            }
        }
        if (bestA == null) return null;
        if (screenPairKnown && best > screenScore - 1.0) return null; // the screen already reads as two real words: need a clearly better reading
        // keep the capital the first word had; names and places get theirs
        string first2 = char.IsUpper(screen1[0]) ? char.ToUpperInvariant(bestA[0]) + bestA[1..] : bestA;
        string newText = pre1 + Recase(first2, lang, prev.StartedSentence) + (bestB != null ? " " + Recase(bestB, lang, false) : "") + suf2;
        string oldText = prev.Text + " " + typed;
        if (newText == oldText || _exceptions.IsBlocked(oldText, newText)) return null;
        return new Decision(ActionKind.Replace, newText, bestB == null ? "space inside a word" : "space in the wrong place")
        {
            ErasePrevious = prev.Text.Length + 1, PreviousText = prev.Text + " ", PreviousWords = 1,
        };
    }

    /// <summary>A word good enough to split into: common (rare dictionary words make spurious splits), single letters only if they are words.</summary>
    private bool IsCommonWord(string w, int lang)
    {
        if (w.Length == 1) return IsSingleLetterWord(w, lang);
        if (_exceptions.Contains(w)) return true;
        int rank = _freq.Rank(lang, w);
        if (w.Length == 2) return rank <= (lang == Dictionaries.LangRu ? 5_000 : 1_000) && (_dicts.Check(lang, w) || lang == Dictionaries.LangRu);
        return rank <= 100_000 && _dicts.Check(lang, w);
    }

    private double Rarity(string w, int lang)
    {
        if (w.Length == 1) return 1.0;
        int rank = _freq.Rank(lang, w);
        return rank == int.MaxValue ? 6 : Math.Log10(Math.Max(rank, 1));
    }

    // ------------------------------------------------------------------ layout collisions and word shape

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
