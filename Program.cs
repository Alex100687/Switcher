using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Switcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--test")
            return SelfTest.Run(args.Skip(1).ToArray());
        if (args.Length > 1 && args[0] == "--ui-smoke")
            return SelfTest.UiSmoke(args[1]);
        if (args.Length > 0 && args[0] == "--windows")
            return SelfTest.Windows();

        // "--wait-for <pid>": started by Restart — let the previous instance release the mutex first
        int w = Array.IndexOf(args, "--wait-for");
        if (w >= 0 && w + 1 < args.Length && int.TryParse(args[w + 1], out int pid))
        {
            try { using var prev = System.Diagnostics.Process.GetProcessById(pid); prev.WaitForExit(5000); } catch { }
        }

        using var mutex = new Mutex(true, @"Local\Switcher_SingleInstance", out bool created);
        if (!created) return 0; // already running
        Settings.MigrateFromLayoutFix();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        Application.ThreadException += (_, e) => Log.Write("UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled: " + e.ExceptionObject);

        using var app = new TrayApp();
        if (app.StartFailed) return 1;
        Application.Run(app);
        return 0;
    }
}

/// <summary>
/// `Switcher.exe --test ghbdtn hello ntrcn` — simulate typing each word (in the layout its first letter belongs to)
/// and print what the engine would do. Output goes to the parent console.
/// </summary>
internal static class SelfTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanExW(char ch, IntPtr dwhkl);

    /// <summary>Open every tab of the settings window off-screen and save screenshots — a layout check without a human.</summary>
    public static int UiSmoke(string pngPrefix)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var settings = Settings.Load();
        var rules = new Rules();
        var dicts = new Dictionaries(); var freq = new Frequencies();
        var engine = new Engine(settings, rules, dicts, freq, new SpellFixer(dicts, freq, rules)); // hook is not installed without Start()
        for (int tab = 0; tab < 3; tab++)
        {
            using var f = new SettingsForm(settings, rules, engine, () => { }, tab) { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000) };
            f.Show();
            for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(25); }
            using var bmp = new System.Drawing.Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
            bmp.Save($"{pngPrefix}-{tab}.png", System.Drawing.Imaging.ImageFormat.Png);
            f.Close();
        }
        return 0;
    }

    /// <summary>
    /// `Switcher.exe --windows` — every program with a window: process name, whether it runs above our integrity level
    /// (Switcher leaves those alone) and the window class (for ExcludedWindowClasses). Prints to the parent console.
    /// </summary>
    public static int Windows()
    {
        Native.AttachConsole(-1);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine();
        var fg = Native.GetForegroundWindow();
        var rows = new List<(string name, uint pid, bool elevated, string cls)>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                IntPtr h;
                try { h = p.MainWindowHandle; } catch { continue; }
                if (h == IntPtr.Zero) continue;
                var info = Processes.Of(h);
                rows.Add((info.Name, (uint)p.Id, info.Elevated, Processes.ClassName(h)));
            }
        }
        foreach (var r in rows.OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"{r.name,-28} {r.pid,7}  {(r.elevated ? "ELEVATED" : "        ")}  {r.cls}");
        var f = Processes.Of(fg);
        Console.WriteLine($"foreground: {f.Name} class={Processes.ClassName(fg)} focus={Processes.ClassName(Injector.FocusWindow(fg))}");
        return 0;
    }

    public static int Run(string[] words)
    {
        Native.AttachConsole(-1);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine();

        var settings = Settings.Load();
        var rules = new Rules();
        var dicts = new Dictionaries();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        dicts.Load();
        var freq = new Frequencies(); freq.Load();
        Console.WriteLine($"dictionaries: {sw.ElapsedMilliseconds} ms");
        var corrector = new Corrector(dicts, rules, settings, freq);
        var speller = new SpellFixer(dicts, freq, rules);

        RuleScope.Current = Environment.GetEnvironmentVariable("SWITCHER_TEST_SCOPE") ?? "";
        var layouts = Layouts.Installed();
        Console.WriteLine("layouts: " + string.Join(", ", layouts.Select(h => $"{Layouts.Name(h)} ({(long)h:X8})")));
        var ru = layouts.FirstOrDefault(h => Native.LangId(h) == Dictionaries.LangRu);
        var en = layouts.FirstOrDefault(h => Native.LangId(h) == Dictionaries.LangEn);
        if (ru == IntPtr.Zero || en == IntPtr.Zero) { Console.WriteLine("need both RU and EN layouts installed"); return 1; }

        if (words.Length == 1 && words[0].StartsWith('@'))
            words = File.ReadAllLines(words[0][1..]).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
        if (words.Length == 0)
            words = new[] { "ghbdtn", "ghbdtn/", "Ghbdtn", "hello", "руддщ", "привет", "ntrcn", "ыефке", "world", "vbh", "мир",
                            "првиет", "hlelo", "tset", "проект", "лол", "хз", "in", "шт", "ok", "да", "lf", "ща", "elif", "foreach", "ghbdtn,", "vjcrdf" };

        foreach (var raw in words)
        {
            int ctx = 0; var w = raw;
            if (w.StartsWith("ru:")) { ctx = Dictionaries.LangRu; w = w[3..]; }
            else if (w.StartsWith("en:")) { ctx = Dictionaries.LangEn; w = w[3..]; }
            if (Environment.GetEnvironmentVariable("SWITCHER_DEBUG") == "1")
                Console.WriteLine("  chars: " + string.Join(" ", w.Select(c => ((int)c).ToString("X4"))));
            bool cyr = w.Any(c => c >= 'А' && c <= 'я' || c == 'ё' || c == 'Ё');
            var typedHkl = cyr ? ru : en;
            var otherHkl = cyr ? en : ru;

            var keys = new List<TypedKey>();
            bool ok = true;
            foreach (var ch in w)
            {
                short r = VkKeyScanExW(ch, typedHkl);
                if (r == -1) { ok = false; break; }
                uint vk = (uint)(r & 0xFF);
                bool shift = (r & 0x100) != 0;
                uint scan = Native.MapVirtualKeyEx(vk, 0, typedHkl);
                keys.Add(new TypedKey(vk, scan, shift, false));
            }
            if (!ok) { Console.WriteLine($"{w,-14} ?  (cannot type this in {Layouts.Name(typedHkl)})"); continue; }

            string typed = WordTracker.Render(keys, typedHkl);
            string alt = WordTracker.Render(keys, otherHkl);
            bool hasDigits = keys.Any(k => WordTracker.IsDigitKey(k.Vk));

            sw.Restart();
            var d = corrector.Decide(typed, Native.LangId(typedHkl), alt, Native.LangId(otherHkl), hasDigits, ctx);
            if (d.Kind == ActionKind.FixSpelling) d = speller.FixEither(typed, typedHkl, alt, otherHkl, settings.AutoSwitchLayout, ctx);
            long ms = sw.ElapsedMilliseconds;

            string verdict = d.Kind switch
            {
                ActionKind.SwitchLayout => $"SWITCH → {d.NewText}",
                ActionKind.FixSpelling => (d.SwitchLayout ? "FIX+SW → " : "FIX    → ") + d.NewText,
                _ => "keep",
            };
            Console.WriteLine($"{typed,-14} alt={alt,-14} {verdict,-24} {ms,4} ms  {d.Reason}");
            if (Environment.GetEnvironmentVariable("SWITCHER_SUGGEST") == "1")
                Console.WriteLine("    suggest: " + string.Join(" | ", dicts.Suggest(Native.LangId(typedHkl), Corrector.StripPunctuation(typed, out _, out _).ToLowerInvariant()).Take(8)));
        }
        return 0;
    }
}
