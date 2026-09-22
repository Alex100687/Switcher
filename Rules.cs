using System.Text;

namespace Switcher;

/// <summary>
/// The application a rule applies to. Rules in the user's files may end with "@process" (process name without .exe);
/// without it a rule is global. The engine sets <see cref="Current"/> to the foreground process before deciding,
/// on whichever thread does the deciding.
/// </summary>
public static class RuleScope
{
    public const string Global = "*";
    [ThreadStatic] private static string? _current;
    public static string Current { get => _current ?? ""; set => _current = value; }

    /// <summary>"word @proc" → ("word", "proc"); "word" → ("word", "*").</summary>
    public static (string text, string scope) Split(string line)
    {
        int at = line.LastIndexOf(" @", StringComparison.Ordinal);
        if (at <= 0) return (line.Trim(), Global);
        var scope = line[(at + 2)..].Trim();
        return (line[..at].Trim(), scope.Length == 0 ? Global : scope);
    }

    public static string Join(string text, string scope) => scope == Global || string.IsNullOrEmpty(scope) ? text : text + " @" + scope;

    public static bool Applies(string scope) => scope == Global || string.Equals(scope, Current, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One user-editable rule, as shown in the learning window.</summary>
public sealed record Rule(RuleKind Kind, string From, string To, string Scope, bool Builtin)
{
    public string Display => Kind == RuleKind.Word ? From : $"{From} → {To}";
}

public enum RuleKind { Word, Blocked, Autocorrect }

/// <summary>
/// Personal words (never touched, and valid targets for a layout switch), rejected replacements ("X → Y" never
/// offered again) and explicit autocorrect rules. Built-in lists ship in dict/, the user's live in %AppData%.
/// All lookups honour <see cref="RuleScope.Current"/>.
/// </summary>
public sealed class Rules
{
    private readonly object _lock = new();
    private readonly List<Rule> _rules = new();
    private readonly Dictionary<string, int> _undoCount = new(StringComparer.OrdinalIgnoreCase);

    public static string WordsPath => Path.Combine(Settings.Dir, "exceptions.txt");
    public static string BlockedPath => Path.Combine(Settings.Dir, "blocked.txt");
    public static string AutocorrectPath => Path.Combine(Settings.Dir, "autocorrect.txt");
    /// <summary>After this many rejected corrections of the same word the user is offered to make it a personal word.</summary>
    public const int UndosToSuggest = 3;

    public event Action? Changed;

    public Rules() => Reload();

    /// <summary>
    /// (Re)read all rule files — at start and whenever the user edits one by hand. A file that cannot be read right
    /// now (an editor is saving it) keeps its previous rules: an empty list in memory would be written over the file
    /// by the next change.
    /// </summary>
    public void Reload()
    {
        lock (_lock)
        {
            var old = _rules.ToList();
            _rules.Clear();
            LoadWords(Path.Combine(Dictionaries.DictDir, "whitelist.txt"), builtin: true, old);
            LoadWords(WordsPath, builtin: false, old);
            LoadPairs(BlockedPath, RuleKind.Blocked, builtin: false, old);
            LoadPairs(Path.Combine(Dictionaries.DictDir, "autocorrect.txt"), RuleKind.Autocorrect, builtin: true, old);
            LoadPairs(AutocorrectPath, RuleKind.Autocorrect, builtin: false, old);
        }
        Changed?.Invoke();
    }

    private void LoadWords(string path, bool builtin, List<Rule> old)
    {
        var lines = ReadLines(path);
        if (lines == null) { _rules.AddRange(old.Where(r => r.Kind == RuleKind.Word && r.Builtin == builtin)); return; }
        foreach (var line in lines)
        {
            var (text, scope) = RuleScope.Split(line);
            if (text.Length > 0) _rules.Add(new Rule(RuleKind.Word, text, "", scope, builtin));
        }
    }

    private void LoadPairs(string path, RuleKind kind, bool builtin, List<Rule> old)
    {
        var lines = ReadLines(path);
        if (lines == null) { _rules.AddRange(old.Where(r => r.Kind == kind && r.Builtin == builtin)); return; }
        foreach (var line in lines)
        {
            var (text, scope) = RuleScope.Split(line);
            int eq = text.IndexOf('=');
            if (eq <= 0) continue;
            var from = text[..eq].Trim(); var to = text[(eq + 1)..].Trim();
            if (from.Length > 0 && to.Length > 0) _rules.Add(new Rule(kind, from, to, scope, builtin));
        }
    }

    /// <summary>Meaningful lines of a rule file; empty if there is no file, null if it exists but cannot be read.</summary>
    private static List<string>? ReadLines(string path)
    {
        string[] lines;
        try
        {
            if (!File.Exists(path)) return new List<string>();
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) { Log.Write($"Rules load failed ({path}): {ex.Message}"); return null; }
        var result = new List<string>(lines.Length);
        foreach (var l in lines)
        {
            var t = l.Trim();
            if (t.Length > 0 && !t.StartsWith('#')) result.Add(t);
        }
        return result;
    }

    // ------------------------------------------------------------------ lookups (scope-aware)

    public bool Contains(string word)
    {
        lock (_lock)
            foreach (var r in _rules)
                if (r.Kind == RuleKind.Word && r.From.Equals(word, StringComparison.OrdinalIgnoreCase) && RuleScope.Applies(r.Scope)) return true;
        return false;
    }

    /// <summary>Case-insensitive, ё = е — the spell fixer compares normalized words, the files keep what was typed.</summary>
    public bool IsBlocked(string from, string to)
    {
        from = Key(from); to = Key(to);
        lock (_lock)
            foreach (var r in _rules)
                if (r.Kind == RuleKind.Blocked && Key(r.From) == from && Key(r.To) == to && RuleScope.Applies(r.Scope)) return true;
        return false;
    }

    private static string Key(string s) => s.Trim().ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Explicit replacement; an app-specific rule beats a global one.</summary>
    public bool TryAutocorrect(string word, out string replacement)
    {
        replacement = "";
        bool found = false;
        lock (_lock)
            foreach (var r in _rules)
            {
                if (r.Kind != RuleKind.Autocorrect || !r.From.Equals(word, StringComparison.OrdinalIgnoreCase) || !RuleScope.Applies(r.Scope)) continue;
                if (!found || r.Scope != RuleScope.Global) { replacement = r.To; found = true; }
            }
        return found;
    }

    // ------------------------------------------------------------------ learning

    /// <summary>
    /// The user undid "from → to": remember the rejected pair (globally). <paramref name="word"/> is what the user had
    /// typed — it differs from <paramref name="from"/> when the fix was made in the other layout. Returns how many times
    /// corrections of that word have been undone — the caller offers to make it a personal word at <see cref="UndosToSuggest"/>.
    /// </summary>
    public int Reject(string from, string to, string? word = null)
    {
        from = from.Trim().ToLowerInvariant(); to = to.Trim().ToLowerInvariant();
        word = (word ?? from).Trim().ToLowerInvariant();
        if (from.Length == 0 || to.Length == 0) return 0;
        int count;
        lock (_lock)
        {
            if (!_rules.Any(r => r.Kind == RuleKind.Blocked && Key(r.From) == Key(from) && Key(r.To) == Key(to) && r.Scope == RuleScope.Global))
                AddLocked(new Rule(RuleKind.Blocked, from, to, RuleScope.Global, false));
            count = _undoCount[word] = _undoCount.TryGetValue(word, out var n) ? n + 1 : 1;
        }
        Changed?.Invoke();
        return count;
    }

    public void Add(Rule rule)
    {
        lock (_lock) AddLocked(rule);
        Changed?.Invoke();
    }

    /// <summary>Personal word, global scope (the common case from the tray / suggestion).</summary>
    public void AddWord(string word) => Add(new Rule(RuleKind.Word, word.Trim().ToLowerInvariant(), "", RuleScope.Global, false));

    private void AddLocked(Rule rule)
    {
        if (rule.Builtin) return;
        _rules.RemoveAll(r => r.Kind == rule.Kind && !r.Builtin && r.From.Equals(rule.From, StringComparison.OrdinalIgnoreCase)
                              && r.To.Equals(rule.To, StringComparison.OrdinalIgnoreCase) && r.Scope.Equals(rule.Scope, StringComparison.OrdinalIgnoreCase));
        _rules.Add(rule);
        SaveLocked(rule.Kind);
    }

    public void Remove(Rule rule)
    {
        lock (_lock)
        {
            if (_rules.Remove(rule)) SaveLocked(rule.Kind);
        }
        Changed?.Invoke();
    }

    public void SetScope(Rule rule, string scope)
    {
        lock (_lock)
        {
            int i = _rules.IndexOf(rule);
            if (i < 0) return;
            _rules[i] = rule with { Scope = string.IsNullOrWhiteSpace(scope) ? RuleScope.Global : scope.Trim() };
            SaveLocked(rule.Kind);
        }
        Changed?.Invoke();
    }

    /// <summary>Delete everything the user taught the program (built-in lists stay).</summary>
    public void ForgetAll()
    {
        lock (_lock)
        {
            _rules.RemoveAll(r => !r.Builtin);
            _undoCount.Clear();
            foreach (var k in new[] { RuleKind.Word, RuleKind.Blocked, RuleKind.Autocorrect }) SaveLocked(k);
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<Rule> Snapshot() { lock (_lock) return _rules.ToArray(); }

    private void SaveLocked(RuleKind kind)
    {
        var (path, header) = kind switch
        {
            RuleKind.Word => (WordsPath, "# Личный словарь: слова, которые не трогать. Строка может заканчиваться на @имя_процесса — правило только там."),
            RuleKind.Blocked => (BlockedPath, "# Отклонённые замены: что_было = на_что_не_менять. Можно добавить @имя_процесса."),
            _ => (AutocorrectPath, "# Свои автозамены: что_набрано = на_что_заменить. Можно добавить @имя_процесса."),
        };
        var sb = new StringBuilder().AppendLine(header);
        foreach (var r in _rules)
            if (r.Kind == kind && !r.Builtin)
                sb.AppendLine(RuleScope.Join(r.Kind == RuleKind.Word ? r.From : $"{r.From} = {r.To}", r.Scope));
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            Settings.AtomicWrite(path, sb.ToString());
        }
        catch (Exception ex) { Log.Write($"Rules save failed ({path}): {ex.Message}"); }
    }
}
