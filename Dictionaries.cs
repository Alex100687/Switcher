using WeCantSpell.Hunspell;

namespace Switcher;

/// <summary>Hunspell dictionaries keyed by Windows language id (0x0419 = ru, 0x0409 = en).</summary>
public sealed class Dictionaries
{
    public const int LangRu = 0x0419;
    public const int LangEn = 0x0409;

    private readonly Dictionary<int, WordList> _lists = new();
    /// <summary>Dictionary roots that are not lowercase, by their lowercase form: "сша" → "США", "youtube" → "YouTube".</summary>
    private readonly Dictionary<int, Dictionary<string, string>> _cased = new();
    public bool IsLoaded { get; private set; }

    public static string DictDir => Path.Combine(AppContext.BaseDirectory, "dict");

    public void Load()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _lists[LangRu] = WordList.CreateFromFiles(Path.Combine(DictDir, "ru_RU.dic"), Path.Combine(DictDir, "ru_RU.aff"));
        _lists[LangEn] = WordList.CreateFromFiles(Path.Combine(DictDir, "en_US.dic"), Path.Combine(DictDir, "en_US.aff"));
        _cased[LangRu] = CasedRoots(Path.Combine(DictDir, "ru_RU.dic"));
        _cased[LangEn] = CasedRoots(Path.Combine(DictDir, "en_US.dic"));
        IsLoaded = true;
        Log.Write($"Dictionaries loaded in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Hunspell answers "is this spelled right", not "how is it capitalised". Title-case words ("Москва") are found by
    /// checking the title-cased form, which also covers their inflections; abbreviations and mixed case ("США",
    /// "iPhone") only by their root as written in the .dic file.
    /// </summary>
    private static Dictionary<string, string> CasedRoots(string dicPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(dicPath).Skip(1))
        {
            int slash = line.IndexOf('/');
            var root = (slash >= 0 ? line[..slash] : line).Trim();
            if (root.Length < 2 || !root.Any(char.IsUpper)) continue;
            map.TryAdd(root.ToLowerInvariant(), root);
        }
        return map;
    }

    public bool Supports(int langId) => _lists.ContainsKey(langId);

    /// <summary>
    /// How the dictionary spells a word that was typed in lowercase, when that is not lowercase: "москва" → "Москва",
    /// "сша" → "США", "youtube" → "YouTube", "санкт-петербург" → "Санкт-Петербург", English "i" → "I".
    /// Null when the lowercase word is itself a word ("роза", "лев") or the dictionary does not know it.
    /// </summary>
    public string? ProperCase(int langId, string lower)
    {
        if (lower.Length == 0 || !_lists.TryGetValue(langId, out var list)) return null;
        if (langId == LangEn && lower == "i") return "I";
        if (Exact(list, langId, lower)) return null;
        if (_cased.TryGetValue(langId, out var map) && map.TryGetValue(lower, out var root))
            return lower.Length > 2 || root.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? root : null;
        // two letters: only abbreviations ("рф" → "РФ"); "Th", "Mo" (weekdays) are not what "th", "mo" mean
        if (lower.Length <= 2) return null;
        var title = char.ToUpperInvariant(lower[0]) + lower[1..];
        if (Exact(list, langId, title)) return title;
        if (lower.Contains('-'))
        {
            // each part on its own: "москва-река" → "Москва-река", "санкт-петербург" → "Санкт-Петербург"
            var parts = lower.Split('-');
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) return null;
                if (Exact(list, langId, parts[i])) continue;
                var p = ProperCase(langId, parts[i]);
                if (p == null) return null;
                parts[i] = p;
                changed = true;
            }
            if (changed) return string.Join('-', parts);
        }
        return null;
    }

    /// <summary>Hunspell's own verdict, case rules included ("москва" is wrong, "Москва" right); ё may be typed as е.</summary>
    private static bool Exact(WordList list, int langId, string word) =>
        list.Check(word) || (langId == LangRu && word.Contains('е') && list.Check(word.Replace('е', 'ё')));

    /// <summary>True if the word is spelled correctly in the given language.</summary>
    public bool Check(int langId, string word)
    {
        if (!_lists.TryGetValue(langId, out var list)) return false;
        if (list.Check(word)) return true;
        // Hunspell accepts "Word" for "word" but not "WORD"/"wORD"; be lenient on case.
        var lower = word.ToLowerInvariant();
        if (lower != word && list.Check(lower)) return true;
        // proper nouns are stored capitalised ("Москва"), the user may type them in lowercase
        var title = char.ToUpperInvariant(lower[0]) + lower[1..];
        if (title != word && list.Check(title)) return true;
        // abbreviations and mixed case typed in lowercase: "сша", "youtube". Not two letters: English has hundreds
        // of two-letter codes (NJ, NS, TN) that would make "yt" (= "не") an English word.
        if (lower.Length > 2 && _cased.TryGetValue(langId, out var map) && map.ContainsKey(lower)) return true;
        // Russian: treat е/ё as interchangeable when typing.
        if (langId == LangRu && lower.Contains('е'))
        {
            var yo = lower.Replace('е', 'ё');
            if (yo != lower && list.Check(yo)) return true;
        }
        return false;
    }

    public IEnumerable<string> Suggest(int langId, string word)
    {
        if (!_lists.TryGetValue(langId, out var list)) return Array.Empty<string>();
        try { return list.Suggest(word); }
        catch (Exception ex) { Log.Write("Suggest failed: " + ex.Message); return Array.Empty<string>(); }
    }
}
