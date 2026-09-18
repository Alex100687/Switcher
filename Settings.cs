using System.Text.Json;

using System.Text.Json.Serialization;

namespace Switcher;

public sealed class Settings

{

    /// <summary>%AppData%\Switcher, or SWITCHER_DATA_DIR if set (portable / testing).</summary>

    public static string Dir => Environment.GetEnvironmentVariable("SWITCHER_DATA_DIR") is { Length: > 0 } d

        ? d

        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Switcher");

    /// <summary>The program used to be called LayoutFix: pick up its settings, exceptions and autocorrect once.</summary>
    public static void MigrateFromLayoutFix()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("SWITCHER_DATA_DIR") is { Length: > 0 }) return;
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LayoutFix");
            if (Directory.Exists(old) && !Directory.Exists(Dir)) Directory.Move(old, Dir);
        }
        catch (Exception ex) { Log.Write("Migration from LayoutFix failed: " + ex.Message); }
    }

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static string ExceptionsPath => Path.Combine(Dir, "exceptions.txt");

    public static string LogPath => Path.Combine(Dir, "log.txt");

    /// <summary>Bumped when a default changes; old files get the affected fields migrated in <see cref="Load"/>.</summary>

    public int SettingsVersion { get; set; } // 0 = file written before versioning

    public const int CurrentVersion = 2;

    /// <summary>Master switch.</summary>

    public bool Enabled { get; set; } = true;

    /// <summary>Automatically switch layout and retype a word typed in the wrong layout.</summary>

    public bool AutoSwitchLayout { get; set; } = true;

    /// <summary>Automatically fix single-edit typos in words that exist in no dictionary.</summary>

    public bool AutoFixSpelling { get; set; } = true;

    /// <summary>Shortest word (letters only) that may be auto-switched.</summary>

    public int MinWordLength { get; set; } = 2;

    /// <summary>Shortest word that may be spell-fixed.</summary>

    public int MinSpellFixLength { get; set; } = 3;

    /// <summary>

    /// If an app ignores our layout-change request, press the system toggle hotkey (Alt+Shift / Ctrl+Shift) instead.

    /// Off by default: an app that merely processes the request late would end up flipped twice.

    /// </summary>

    public bool ToggleHotkeyIfIgnored { get; set; } = false;

    /// <summary>Play a short sound when a word is changed.</summary>

    public bool Beep { get; set; } = false;

    /// <summary>Write every action to log.txt (useful for finding false positives).</summary>

    public bool LogActions { get; set; } = true;

    /// <summary>Hotkey: convert last word / undo. Format "Ctrl+Shift+Key", e.g. "Pause", "Ctrl+Shift+Space".</summary>

    public string Hotkey { get; set; } = "Pause";

    /// <summary>Process names (without .exe) where the program does nothing.</summary>

    public List<string> ExcludedProcesses { get; set; } = new()

    {

        "Code", "devenv", "rider64", "idea64", "sublime_text", "notepad++",

        "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "mintty", "alacritty", "wezterm-gui",

        "Unity", "UnityHub", "blender",

        "mstsc", "vmware", "VirtualBoxVM",

    };

    public static Settings Load()

    {

        try

        {

            if (File.Exists(FilePath))

            {

                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);

                if (s != null)

                {

                    if (s.SettingsVersion < 2 && s.MinSpellFixLength == 4) s.MinSpellFixLength = 3; // v2: broader spell fixing

                    if (s.SettingsVersion != CurrentVersion) { s.SettingsVersion = CurrentVersion; s.Save(); }

                    return s;

                }

            }

        }

        catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }

        var def = new Settings();

        def.Save();

        return def;

    }

    public void Save()

    {

        try

        {

            SettingsVersion = CurrentVersion;

            Directory.CreateDirectory(Dir);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));

        }

        catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }

    }

    private static readonly JsonSerializerOptions JsonOptions = new()

    {

        WriteIndented = true,

        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        ReadCommentHandling = JsonCommentHandling.Skip,

        AllowTrailingCommas = true,

    };

}

/// <summary>Words the user has told us to leave alone (one per line, lowercase).</summary>

public sealed class Exceptions

{

    private readonly HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();

    public Exceptions()

    {

        // built-in whitelist shipped with the program (tech words etc.) + the user's own list

        LoadFile(Path.Combine(Dictionaries.DictDir, "whitelist.txt"));

        LoadFile(Settings.ExceptionsPath);

    }

    private void LoadFile(string path)

    {

        try

        {

            if (!File.Exists(path)) return;

            foreach (var line in File.ReadAllLines(path))

            {

                var w = line.Trim();

                if (w.Length > 0 && !w.StartsWith('#')) _words.Add(w);

            }

        }

        catch (Exception ex) { Log.Write($"Word list load failed ({path}): {ex.Message}"); }

    }

    public bool Contains(string word) { lock (_lock) return _words.Contains(word); }

    public void Add(string word)

    {

        word = word.Trim().ToLowerInvariant();

        if (word.Length == 0) return;

        lock (_lock)

        {

            if (!_words.Add(word)) return;

            try

            {

                Directory.CreateDirectory(Settings.Dir);

                File.AppendAllText(Settings.ExceptionsPath, word + Environment.NewLine);

            }

            catch (Exception ex) { Log.Write("Exceptions save failed: " + ex.Message); }

        }

    }

}

public static class Log

{

    private static readonly object _lock = new();

    public static bool Enabled = true;

    public static void Write(string message)

    {

        if (!Enabled) return;

        try

        {

            lock (_lock)

            {

                Directory.CreateDirectory(Settings.Dir);

                // keep the log from growing without bound

                if (File.Exists(Settings.LogPath) && new FileInfo(Settings.LogPath).Length > 2_000_000)

                    File.Move(Settings.LogPath, Settings.LogPath + ".old", overwrite: true);

                File.AppendAllText(Settings.LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");

            }

        }

        catch { /* logging must never break the app */ }

    }

}

