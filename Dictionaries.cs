using WeCantSpell.Hunspell;

namespace Switcher;

/// <summary>Hunspell dictionaries keyed by Windows language id (0x0419 = ru, 0x0409 = en).</summary>
public sealed class Dictionaries
{
    public const int LangRu = 0x0419;
    public const int LangEn = 0x0409;

    private readonly Dictionary<int, WordList> _lists = new();
    public bool IsLoaded { get; private set; }

    public static string DictDir => Path.Combine(AppContext.BaseDirectory, "dict");

    public void Load()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _lists[LangRu] = WordList.CreateFromFiles(Path.Combine(DictDir, "ru_RU.dic"), Path.Combine(DictDir, "ru_RU.aff"));
        _lists[LangEn] = WordList.CreateFromFiles(Path.Combine(DictDir, "en_US.dic"), Path.Combine(DictDir, "en_US.aff"));
        IsLoaded = true;
        Log.Write($"Dictionaries loaded in {sw.ElapsedMilliseconds} ms");
    }

    public bool Supports(int langId) => _lists.ContainsKey(langId);

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
