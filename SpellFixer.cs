namespace Switcher;



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

    public int Rank(int lang, string word) => RankNormalized(lang, word.ToLowerInvariant().Replace('ё', 'е'));



    /// <summary>Same, for a word that is already lowercase with ё→е (hot path of candidate generation).</summary>

    public int RankNormalized(int lang, string word)

    {

        if (!_ranks.TryGetValue(lang, out var d)) return int.MaxValue;

        return d.TryGetValue(word, out var r) ? r : int.MaxValue;

    }

}



/// <summary>Explicit "wrong = right" replacements: built-in dict/autocorrect.txt + user's %AppData%\Switcher\autocorrect.txt.</summary>

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



    private static readonly bool FixDebug = Environment.GetEnvironmentVariable("SWITCHER_FIXDEBUG") == "1";



    /// <summary>Typed words at least this frequent are treated as real (colloquial) words and left alone.</summary>

    public static int KnownRankLimit(int lang) => lang == Dictionaries.LangRu ? 10_000 : 5_000;



    /// <summary>

    /// The word is unknown in both layouts: try to fix it as typed and as it would read in the other layout

    /// (";spym" → "жызнь" → "жизнь"), and take the better-scoring fix.

    /// </summary>

    public Decision FixEither(string typed, IntPtr layout, string alt, IntPtr other, bool allowSwitch, int contextLang = 0)

    {

        if (!allowSwitch || other == IntPtr.Zero || alt.Length == 0) return Fix(typed, layout);

        var typedTask = Task.Run(() => Fix(typed, layout));

        var asAlt = Fix(alt, other);

        var asTyped = typedTask.GetAwaiter().GetResult();

        // two guesses stacked (wrong layout AND a typo) must be convincing: a good score and a real-sized word

        if (asAlt.Kind == ActionKind.FixSpelling

            && (asAlt.Score > 1.3 || Corrector.StripPunctuation(asAlt.NewText, out _, out _).Length < 4))

            asAlt = Decision.Keep;

        if (asAlt.Kind != ActionKind.FixSpelling) return asTyped;

        if (asTyped.Kind != ActionKind.FixSpelling) return asAlt with { SwitchLayout = true, Reason = asAlt.Reason + ", switch" };

        // two hypotheses at once (wrong layout AND a typo): the language of the surrounding text gets a head start,

        // otherwise the current layout wins ties

        double bias = contextLang == Native.LangId(other) ? -0.3 : contextLang == Native.LangId(layout) ? 0.3 : 0.1;

        if (asTyped.Score <= asAlt.Score + bias) return asTyped;

        return asAlt with { SwitchLayout = true, Reason = asAlt.Reason + ", switch" };

    }



    /// <summary>JIT and caches: run once after loading so the first real correction is not the slow one.</summary>

    public void WarmUp()

    {

        foreach (var h in Layouts.Installed())

        {

            int lang = Native.LangId(h);

            if (lang == Dictionaries.LangRu) { Fix("првиет", h); Fix("здраствуйти", h); }

            if (lang == Dictionaries.LangEn) { Fix("hlelo", h); Fix("definatley", h); }

        }

    }



    public Decision Fix(string typed, IntPtr hkl)

    {

        int lang = Native.LangId(hkl);

        var core = Corrector.StripPunctuation(typed, out var prefix, out var suffix);

        if (core.Length < 3 || !Corrector.IsPureLetters(core)) return Decision.Keep;

        var lower = core.ToLowerInvariant();



        if (_auto.TryGet(lower, out var explicitFix))

            return Result(core, explicitFix, prefix, suffix, lang, "autocorrect", 0);



        if (_freq.Rank(lang, lower) <= KnownRankLimit(lang)) return Decision.Keep; // frequent colloquial word



        var norm = EditCost.Normalize(lower);

        bool capitalized = char.IsUpper(core[0]);

        var best = ChooseBest(norm, lang, hkl, capitalized, out var reason, out var score);

        if (best == null) return Decision.Keep;

        return Result(core, best, prefix, suffix, lang, reason, score);

    }



    private static Decision Result(string core, string fix, string prefix, string suffix, int lang, string reason, double score)

    {

        var cased = Corrector.MatchCase(core, fix);

        if (cased == core) return Decision.Keep;

        return new Decision(ActionKind.FixSpelling, prefix + cased + suffix, $"'{core}' → '{cased}' ({Corrector.LangName(lang)}, {reason})", score);

    }



    private sealed record Cand(string Word, double Cost, int Rank)

    {

        public double Score => Cost + RankPenalty(Rank);

        // 0 for the most frequent word, ~0.5 at rank 10 000, then steeper: a rare target needs a very cheap edit

        private static double RankPenalty(int r) =>
            r == int.MaxValue ? 0.85
            : 0.12 * Math.Log10(r) + (r > 20_000 ? 0.3 * (Math.Log10(r) - 4.3) : 0) + (r > 50_000 ? 0.4 * (Math.Log10(r) - 4.7) : 0);

    }



    private string? ChooseBest(string norm, int lang, IntPtr hkl, bool capitalized, out string reason, out double score)

    {

        reason = ""; score = 0;

        var seen = new Dictionary<string, int>(StringComparer.Ordinal); // normalized word → index in cands

        var cands = new List<Cand>();



        // the same word can be reached by several paths — keep the cheapest

        void Add(string word, double? fixedCost = null)

        {

            var w = EditCost.Normalize(word.ToLowerInvariant());

            if (w == norm) return;

            double cost = fixedCost ?? EditCost.Distance(norm, w, lang, hkl);

            if (seen.TryGetValue(w, out var idx))

            {

                if (cost < cands[idx].Cost) cands[idx] = cands[idx] with { Cost = cost };

                return;

            }

            seen[w] = cands.Count;

            cands.Add(new Cand(word, cost, _freq.Rank(lang, w)));

        }



        // generated variants are accepted only if they are in the frequency list anyway, and that lookup is ~1000×

        // cheaper than Hunspell — so it goes first

        bool IsWord(string v) => _freq.RankNormalized(lang, v) != int.MaxValue && _dicts.Check(lang, v);



        // 1b. A missed space: "инужно" → "и нужно", "вобщем" → "в общем". Russian only (English compounds are

        //     too often real words: raycast, webhook); both halves must be common, a tiny first word is typical.

        if (lang == Dictionaries.LangRu)

            for (int i = 1; i < norm.Length; i++)

            {

                string a = norm[..i], b = norm[i..];

                int ra = _freq.Rank(lang, a), rb = _freq.Rank(lang, b);

                if (ra > 20_000 || rb > 20_000) continue;

                if (!_dicts.Check(lang, a) || !_dicts.Check(lang, b)) continue;

                double cost = ra <= 1_000 && rb <= 1_000 ? 0.9 : 1.25;

                var split = a + " " + b;

                if (!seen.ContainsKey(split)) { seen[split] = cands.Count; cands.Add(new Cand(split, cost, Math.Max(ra, rb))); }

            }



        // 2. Our own: up to two cheap orthographic substitutions (Hunspell rarely finds "малако" → "молоко")

        foreach (var c in EditCost.CheapVariants(norm, lang, maxSubs: 2, limit: 400))

            if (IsWord(c)) Add(c);



        // 3. Every single edit (Hunspell caps its list at ~15 and misses some), then two edits where the first one

        //    is a likely slip — transposition, cheap substitution, doubled letter, neighbouring key — and the second

        //    anything. Overlapping transpositions ("првеит" → "привет") are cheaper as two steps than the DP thinks,

        //    so a two-step candidate gets min(DP, step1 + step2).

        var single = new HashSet<string>(StringComparer.Ordinal);

        foreach (var v in EditCost.SingleEdits(norm, lang))

            if (single.Add(v) && IsWord(v)) Add(v);

        if (norm.Length >= 5)

        {

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var tried = new HashSet<string>(StringComparer.Ordinal);

            int mids = 0;

            foreach (var (mid, stepCost) in EditCost.LikelySlips(norm, lang, hkl))

            {

                if (sw.ElapsedMilliseconds > 40) { if (FixDebug) Log.Write($"  two-step budget hit after {mids} intermediates"); break; }

                if (!tried.Add(mid)) continue;

                mids++;

                foreach (var v in EditCost.SingleEdits(mid, lang))

                {

                    if (v == norm || !single.Add(v)) continue;

                    if (!IsWord(v)) continue;

                    double twoStep = stepCost + EditCost.Distance(mid, v, lang, hkl);

                    Add(v, Math.Min(EditCost.Distance(norm, v, lang, hkl), twoStep));

                }

            }

        }



        if (FixDebug) Log.Write($"  fast stages: {cands.Count} cands, best {(cands.Count > 0 ? cands.Min(c => c.Score).ToString("0.00") : "-")}: " + string.Join(", ", cands.OrderBy(c => c.Score).Take(5).Select(c => $"{c.Word}({c.Cost:0.00}/{c.Rank})")));

        // 4. Hunspell's own suggestions (REP table: phonetic spellings, n-gram guesses) — slow (30–100 ms),

        //    so only when the fast generators have not already found a convincing candidate

        if (cands.Count == 0 || cands.Min(c => c.Score) > 1.4)

            foreach (var s in _dicts.Suggest(lang, norm))

                if (s.Length > 0 && !s.Contains('-') && !s.Contains(' ')) Add(s);



        if (cands.Count == 0) return null;

        cands.Sort((a, b) => a.Score.CompareTo(b.Score));

        var best = cands[0];



        int len = norm.Length;

        // longer words tolerate two edits; short ones collide with too much

        double maxScore = len <= 5 ? 1.5 : len <= 7 ? 1.7 : 1.9;

        if (best.Rank == int.MaxValue) return null;                    // correcting towards a word nobody uses is a guess

        if (best.Score > maxScore) return null;                        // too far and/or too rare

        // short words collide with jargon and commands (sed, awk, sudo, хайр) — demand a cheap edit to a common word

        if (len == 3 && (best.Cost > 0.6 || best.Rank > (lang == Dictionaries.LangEn ? 300 : 5_000))) return null;

        if (len == 4 && (best.Cost > 1.0 || best.Rank > 10_000)) return null;

        if (capitalized && best.Rank > 10_000) return null;            // probably a name we don't know (Вельск ≠ Вольск)



        reason = $"cost {best.Cost:0.00}, rank {best.Rank}";

        score = best.Score;

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

        "ae", "ei", "ai", "ou", "sz", "cs", "ck", "yi", "ao", "ui", "gj", "fv");



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

        if (IsCons(c, lang) && k > 0 && IsCons(typed[k - 1], lang) && k + 1 < typed.Length && IsCons(typed[k + 1], lang))

            return Neighbour; // an extra consonant inside a cluster: интерестно (but not вторы → воры)

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



    private const string AlphaRu = "абвгдежзийклмнопрстуфхцчшщъыьэюя";

    private const string AlphaEn = "abcdefghijklmnopqrstuvwxyz";



    /// <summary>Every string one edit away: deletions, transpositions, substitutions, insertions.</summary>

    public static IEnumerable<string> SingleEdits(string w, int lang)

    {

        string alpha = lang == Dictionaries.LangRu ? AlphaRu : AlphaEn;

        int n = w.Length;

        for (int i = 0; i < n; i++) yield return w.Remove(i, 1);

        for (int i = 0; i + 1 < n; i++)

            if (w[i] != w[i + 1]) yield return w[..i] + w[i + 1] + w[i] + w[(i + 2)..];

        for (int i = 0; i < n; i++)

            foreach (var c in alpha)

                if (c != w[i]) yield return w[..i] + c + w[(i + 1)..];

        for (int i = 0; i <= n; i++)

            foreach (var c in alpha)

                yield return w[..i] + c + w[i..];

    }



    /// <summary>The first of two edits: only the kinds of slip a fast typist actually makes, with their cost.</summary>

    public static IEnumerable<(string word, double cost)> LikelySlips(string w, int lang, IntPtr hkl)

    {

        var table = lang == Dictionaries.LangRu ? CheapRu : CheapEn;

        int n = w.Length;

        for (int i = 0; i + 1 < n; i++)                                              // transposition

            if (w[i] != w[i + 1]) yield return (w[..i] + w[i + 1] + w[i] + w[(i + 2)..], Transposition);

        for (int i = 0; i < n; i++)                                                  // cheap substitution

            if (table.TryGetValue(w[i], out var opts))

                foreach (var c in opts) yield return (w[..i] + c + w[(i + 1)..], Cheap);

        for (int i = 0; i + 1 < n; i++)                                              // doubled letter

            if (w[i] == w[i + 1]) yield return (w.Remove(i, 1), Cheap);

        // a missing letter is not a first step: "missing + X" is found as "X + missing" (order does not matter)

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

