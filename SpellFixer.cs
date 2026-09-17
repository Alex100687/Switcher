namespace LayoutFix;

/// <summary>Word-form frequency ranks (1 = most frequent) per language, from dict/{ru,en}_freq.txt.</summary>
public sealed class Frequencies
{
    private readonly Dictionary<int, Dictionary<string, int>> _ranks = new();

    public void Load()
    {
        LoadOne(Dictionaries.LangRu, Path.Combine(Dictionaries.DictDir, "ru_freq.txt"));
        LoadOne(Dictionaries.LangEn, Path.Combine(Dictionaries.DictDir, "en_freq.txt"));
    }

    private void LoadOne(int lang, string path)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            int rank = 0;
            foreach (var line in File.ReadLines(path))
            {
                var w = line.Trim();
                if (w.Length == 0) continue;
                rank++;
                d.TryAdd(w.Replace('ё', 'е'), rank);
            }
        }
        else Log.Write("Frequency list missing: " + path);
        _ranks[lang] = d;
    }

    /// <summary>Rank of the word (case-insensitive, ё=е) or int.MaxValue if unknown.</summary>
    public int Rank(int lang, string word)
    {
        if (!_ranks.TryGetValue(lang, out var d)) return int.MaxValue;
        return d.TryGetValue(word.ToLowerInvariant().Replace('ё', 'е'), out var r) ? r : int.MaxValue;
    }
}

/// <summary>Explicit "wrong = right" replacements: built-in dict/autocorrect.txt + user's %AppData%\LayoutFix\autocorrect.txt.</summary>
public sealed class Autocorrect
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public static string UserPath => Path.Combine(Settings.Dir, "autocorrect.txt");

    public Autocorrect()
    {
        LoadFile(Path.Combine(Dictionaries.DictDir, "autocorrect.txt"));
        LoadFile(UserPath);
    }

    private void LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                var from = t[..eq].Trim();
                var to = t[(eq + 1)..].Trim();
                if (from.Length > 0 && to.Length > 0) _map[from] = to;
            }
        }
        catch (Exception ex) { Log.Write($"Autocorrect load failed ({path}): {ex.Message}"); }
    }

    public bool TryGet(string word, out string replacement) => _map.TryGetValue(word, out replacement!);
}

/// <summary>
/// Picks the best correction for a misspelled word. Candidates come from Hunspell and from combinations of
/// "cheap" orthographic substitutions; they are scored by a weighted edit distance (typical spelling errors
/// cost less than random ones) and tie-broken by word frequency.
/// </summary>
public sealed class SpellFixer
{
    private readonly Dictionaries _dicts;
    private readonly Frequencies _freq;
    private readonly Autocorrect _auto;

    public SpellFixer(Dictionaries dicts, Frequencies freq, Autocorrect auto)
    {
        _dicts = dicts;
        _freq = freq;
        _auto = auto;
    }

    /// <summary>Typed words at least this frequent are treated as real (colloquial) words and left alone.</summary>
    private static int KnownRankLimit(int lang) => lang == Dictionaries.LangRu ? 10_000 : 5_000;

    public Decision Fix(string typed, IntPtr hkl)
    {
        int lang = Native.LangId(hkl);
        var core = Corrector.StripPunctuation(typed, out var prefix, out var suffix);
        if (core.Length < 3) return Decision.Keep;
        var lower = core.ToLowerInvariant();

        if (_auto.TryGet(lower, out var explicitFix))
            return Result(core, explicitFix, prefix, suffix, lang, "autocorrect");

        if (_freq.Rank(lang, lower) <= KnownRankLimit(lang)) return Decision.Keep; // frequent colloquial word

        var norm = EditCost.Normalize(lower);
        bool capitalized = char.IsUpper(core[0]);
        var best = ChooseBest(norm, lang, hkl, capitalized, out var reason);
        if (best == null) return Decision.Keep;
        return Result(core, best, prefix, suffix, lang, reason);
    }

    private static Decision Result(string core, string fix, string prefix, string suffix, int lang, string reason)
    {
        var cased = Corrector.MatchCase(core, fix);
        if (cased == core) return Decision.Keep;
        return new Decision(ActionKind.FixSpelling, prefix + cased + suffix, $"'{core}' → '{cased}' ({Corrector.LangName(lang)}, {reason})");
    }

    private sealed record Cand(string Word, double Cost, int Rank)
    {
        public double Score => Cost + RankPenalty(Rank);
        // 0 for the most frequent word, ~0.5 at rank 10 000, 0.85 for a word not in the list
        private static double RankPenalty(int r) => r == int.MaxValue ? 0.85 : 0.12 * Math.Log10(r);
    }

    private string? ChooseBest(string norm, int lang, IntPtr hkl, bool capitalized, out string reason)
    {
        reason = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cands = new List<Cand>();

        void Add(string word, double? fixedCost = null)
        {
            var w = EditCost.Normalize(word.ToLowerInvariant());
            if (w == norm || !seen.Add(w)) return;
            double cost = fixedCost ?? EditCost.Distance(norm, w, lang, hkl);
            cands.Add(new Cand(word, cost, _freq.Rank(lang, w)));
        }

        // 1. Hunspell's own suggestions
        foreach (var s in _dicts.Suggest(lang, norm))
        {
            if (s.Length == 0 || s.Contains('-')) continue;
            if (s.Contains(' '))
            {
                // "в общем" style splits: Russian only, both halves must be common words
                if (lang != Dictionaries.LangRu) continue;
                var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || string.Concat(parts) != norm) continue;
                if (parts.Any(p => _freq.Rank(lang, p) > 20_000)) continue;
                var w = EditCost.Normalize(s.ToLowerInvariant());
                if (seen.Add(w)) cands.Add(new Cand(s, 1.25, parts.Max(p => _freq.Rank(lang, p))));
                continue;
            }
            Add(s);
        }

        // 2. Our own: up to two cheap orthographic substitutions (Hunspell rarely finds "малако" → "молоко")
        foreach (var c in EditCost.CheapVariants(norm, lang, maxSubs: 2, limit: 400))
            if (_dicts.Check(lang, c)) Add(c);

        if (cands.Count == 0) return null;
        cands.Sort((a, b) => a.Score.CompareTo(b.Score));
        var best = cands[0];

        int len = norm.Length;
        if (best.Rank == int.MaxValue) return null;                    // correcting towards a word nobody uses is a guess
        if (best.Score > 1.5) return null;                             // too far and/or too rare
        // short words collide with jargon and commands (sed, awk, sudo, хайр) — demand a cheap edit to a common word
        if (len == 3 && (best.Cost > 0.6 || best.Rank > (lang == Dictionaries.LangEn ? 300 : 5_000))) return null;
        if (len == 4 && (best.Cost > 1.0 || best.Rank > 10_000)) return null;
        if (capitalized && best.Rank > 10_000) return null;            // probably a name we don't know (Вельск ≠ Вольск)

        reason = $"cost {best.Cost:0.00}, rank {(best.Rank == int.MaxValue ? "-" : best.Rank.ToString())}";
        return best.Word;
    }
}

/// <summary>Weighted edit distance tuned for spelling errors, plus generation of cheap-substitution variants.</summary>
public static class EditCost
{
    public const double Transposition = 0.6;
    public const double Cheap = 0.5;          // typical orthographic confusion (а/о, е/и, з/с …)
    public const double Neighbour = 0.7;      // adjacent key on the keyboard
    public const double Insert = 0.75;        // user missed a letter
    public const double Delete = 1.0;         // user typed an extra letter
    public const double DeleteLast = 1.2;     // …at the very end: more often a jargon suffix (пивот ≠ пиво)
    public const double Full = 1.0;

    private static readonly Dictionary<char, string> CheapRu = Build(
        "ао", "еи", "иы", "еэ", "ея", "ое", "ую", "ая", "зс", "дт", "бп", "вф", "гк", "жш", "чш", "щш", "хг", "цс", "ьъ", "йи");
    private static readonly Dictionary<char, string> CheapEn = Build(
        "ae", "ei", "ou", "sz", "cs", "ck", "yi", "ao", "ui", "gj", "fv");

    private const string ConsRu = "бвгджзйклмнпрстфхцчшщ";
    private const string ConsEn = "bcdfghjklmnpqrstvwxz";
    private const string Soft = "ьъ";

    private static Dictionary<char, string> Build(params string[] pairs)
    {
        var d = new Dictionary<char, string>();
        foreach (var p in pairs)
            foreach (var c in p)
                foreach (var o in p)
                    if (o != c) d[c] = (d.TryGetValue(c, out var s) ? s : "") + o;
        return d;
    }

    public static string Normalize(string s) => s.Replace('ё', 'е');

    private static bool IsCons(char c, int lang) => (lang == Dictionaries.LangRu ? ConsRu : ConsEn).Contains(c);
    private static bool IsCheapPair(char a, char b, int lang) =>
        (lang == Dictionaries.LangRu ? CheapRu : CheapEn).TryGetValue(a, out var s) && s.Contains(b);

    private static double Sub(char a, char b, int lang, IntPtr hkl)
    {
        if (a == b) return 0;
        if (IsCheapPair(a, b, lang)) return Cheap;
        if (TypoModel.AreNeighbours(a, b, hkl)) return Neighbour;
        return Full;
    }

    /// <summary>Cost of the letter cand[k] missing from the typed word.</summary>
    private static double Ins(string cand, int k, int lang)
    {
        char c = cand[k];
        if ((k > 0 && cand[k - 1] == c) || (k + 1 < cand.Length && cand[k + 1] == c)) return Cheap; // doubling
        if (Soft.Contains(c)) return Cheap;
        if (IsCons(c, lang) && ((k > 0 && IsCons(cand[k - 1], lang)) || (k + 1 < cand.Length && IsCons(cand[k + 1], lang))))
            return Cheap; // unpronounced consonant in a cluster: здраствуйте, сонце, лесница
        return Insert;
    }

    /// <summary>Cost of the extra letter typed[k].</summary>
    private static double Del(string typed, int k, int lang)
    {
        char c = typed[k];
        if ((k > 0 && typed[k - 1] == c) || (k + 1 < typed.Length && typed[k + 1] == c)) return Cheap; // doubled
        if (Soft.Contains(c)) return Cheap;
        if (IsCons(c, lang) && ((k > 0 && IsCons(typed[k - 1], lang)) || (k + 1 < typed.Length && IsCons(typed[k + 1], lang))))
            return Neighbour; // интерестно
        return k == typed.Length - 1 ? DeleteLast : Delete;
    }

    public static double Distance(string typed, string cand, int lang, IntPtr hkl)
    {
        int n = typed.Length, m = cand.Length;
        var d = new double[n + 1, m + 1];
        for (int i = 1; i <= n; i++) d[i, 0] = d[i - 1, 0] + Del(typed, i - 1, lang);
        for (int j = 1; j <= m; j++) d[0, j] = d[0, j - 1] + Ins(cand, j - 1, lang);
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
            {
                double v = d[i - 1, j - 1] + Sub(typed[i - 1], cand[j - 1], lang, hkl);
                v = Math.Min(v, d[i - 1, j] + Del(typed, i - 1, lang));
                v = Math.Min(v, d[i, j - 1] + Ins(cand, j - 1, lang));
                if (i > 1 && j > 1 && typed[i - 1] == cand[j - 2] && typed[i - 2] == cand[j - 1] && typed[i - 1] != typed[i - 2])
                    v = Math.Min(v, d[i - 2, j - 2] + Transposition);
                d[i, j] = v;
            }
        return d[n, m];
    }

    /// <summary>All words reachable from <paramref name="word"/> by 1..maxSubs cheap substitutions.</summary>
    public static IEnumerable<string> CheapVariants(string word, int lang, int maxSubs, int limit)
    {
        var table = lang == Dictionaries.LangRu ? CheapRu : CheapEn;
        var positions = new List<int>();
        for (int i = 0; i < word.Length; i++) if (table.ContainsKey(word[i])) positions.Add(i);

        int produced = 0;
        var chars = word.ToCharArray();
        // single substitutions
        foreach (var i in positions)
            foreach (var o in table[word[i]])
            {
                chars[i] = o;
                yield return new string(chars);
                chars[i] = word[i];
                if (++produced >= limit) yield break;
            }
        if (maxSubs < 2) yield break;
        // pairs
        for (int a = 0; a < positions.Count; a++)
            for (int b = a + 1; b < positions.Count; b++)
            {
                int i = positions[a], j = positions[b];
                foreach (var oi in table[word[i]])
                    foreach (var oj in table[word[j]])
                    {
                        chars[i] = oi; chars[j] = oj;
                        yield return new string(chars);
                        chars[i] = word[i]; chars[j] = word[j];
                        if (++produced >= limit) yield break;
                    }
            }
    }
}
