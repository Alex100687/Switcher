using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Switcher;

public sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Switcher";

    private readonly Settings _settings;
    private readonly Rules _exceptions;
    private readonly Dictionaries _dicts = new();
    private readonly Frequencies _freq = new();
    private readonly NotifyIcon _icon;
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;
    private Engine? _engine;

    private ToolStripMenuItem _miEnabled = null!, _miSwitch = null!, _miSpell = null!, _miBeep = null!, _miLog = null!, _miAutostart = null!, _miStatus = null!, _miPause = null!;
    private SettingsForm? _settingsForm;
    private string? _offeredWord;
    private System.Windows.Forms.Timer? _pauseTimer;

    private void OpenSettings(int tab)
    {
        if (_engine == null) return;
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settings, _exceptions, _engine, UpdateUi, tab);
            _settingsForm.Icon = _iconOn;
        }
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void Pause(int minutes)
    {
        if (_engine == null) return;
        _engine.PausedUntil = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : DateTime.MinValue;
        _pauseTimer ??= new System.Windows.Forms.Timer { Interval = 30_000 };
        _pauseTimer.Tick -= PauseTick; _pauseTimer.Tick += PauseTick;
        _pauseTimer.Enabled = minutes > 0;
        UpdateUi();
    }

    private void PauseTick(object? s, EventArgs e)
    {
        if (_engine != null && !_engine.IsPaused) { _pauseTimer!.Enabled = false; UpdateUi(); }
    }

    /// <summary>The same word's correction was undone several times: ask (a balloon) before making it a personal word.</summary>
    private void OfferWord(string word)
    {
        _offeredWord = word;
        _icon.BalloonTipTitle = "Switcher";
        _icon.BalloonTipText = $"Вы {Rules.UndosToSuggest} раза отменяли исправление слова «{word}». Нажмите, чтобы добавить его в личный словарь — тогда оно не будет исправляться.";
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(10_000);
    }

    public TrayApp()
    {
        _settings = Settings.Load();
        Log.Enabled = true;
        _exceptions = new Rules();

        _iconOn = MakeIcon(Color.FromArgb(0x2B, 0x8A, 0x3E));
        _iconOff = MakeIcon(Color.FromArgb(0x80, 0x80, 0x80));

        _icon = new NotifyIcon
        {
            Icon = _iconOn,
            Text = "Switcher — загрузка словарей…",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (_, _) => OpenSettings(0);
        _icon.BalloonTipClicked += (_, _) =>
        {
            var w = _offeredWord; _offeredWord = null;
            if (w == null) return;
            _exceptions.AddWord(w);
            Log.Write($"'{w}' добавлено в личный словарь по подтверждению пользователя");
        };

        var speller = new SpellFixer(_dicts, _freq, _exceptions);
        _engine = new Engine(_settings, _exceptions, _dicts, _freq, speller);
        _engine.SuggestWord += word => BeginInvokeUi(() => OfferWord(word));
        _engine.Notify += _ => { };
        try
        {
            _engine.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось установить хук клавиатуры:\n" + ex.Message, "Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitThread();
            return;
        }

        Task.Run(() =>
        {
            try { _dicts.Load(); _freq.Load(); Log.Write("Frequencies loaded"); speller.WarmUp(); _engine.Ready = true; }
            catch (Exception ex)
            {
                Log.Write("Dictionary load failed: " + ex);
                BeginInvokeUi(() => MessageBox.Show("Не удалось загрузить словари из папки dict:\n" + ex.Message, "Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error));
                return;
            }
            BeginInvokeUi(UpdateUi);
        });

        UpdateUi();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _miStatus = new ToolStripMenuItem("Switcher") { Enabled = false };
        menu.Items.Add(_miStatus);
        menu.Items.Add(new ToolStripSeparator());

        Add(menu, "Настройки…", () => OpenSettings(0));
        Add(menu, "Обучение (личные слова, отклонённые замены)…", () => OpenSettings(2));
        menu.Items.Add(new ToolStripSeparator());
        _miEnabled = Add(menu, "Включено", ToggleEnabled);
        _miPause = new ToolStripMenuItem("Пауза");
        _miPause.DropDownItems.Add("На 15 минут", null, (_, _) => Pause(15));
        _miPause.DropDownItems.Add("На 1 час", null, (_, _) => Pause(60));
        _miPause.DropDownItems.Add("Возобновить", null, (_, _) => Pause(0));
        menu.Items.Add(_miPause);
        _miSwitch = Add(menu, "Автопереключение раскладки", () => { _settings.AutoSwitchLayout = !_settings.AutoSwitchLayout; Save(); });
        _miSpell = Add(menu, "Автоисправление опечаток", () => { _settings.AutoFixSpelling = !_settings.AutoFixSpelling; Save(); });
        _miBeep = Add(menu, "Звук при исправлении", () => { _settings.Beep = !_settings.Beep; Save(); });
        _miLog = Add(menu, "Записывать замены в лог", () => { _settings.LogActions = !_settings.LogActions; Save(); });
        menu.Items.Add(new ToolStripSeparator());
        _miAutostart = Add(menu, "Запускать при входе в Windows", () => { SetAutostart(!IsAutostart()); UpdateUi(); });
        menu.Items.Add(new ToolStripSeparator());
        Add(menu, "Открыть настройки (settings.json)", () => Open(Settings.FilePath));
        Add(menu, "Открыть автозамены (autocorrect.txt)", () => { EnsureFile(Rules.AutocorrectPath, "# что_набрано = на_что_заменить" + Environment.NewLine); Open(Rules.AutocorrectPath); });
        Add(menu, "Открыть отклонённые замены (blocked.txt)", () => { EnsureFile(Rules.BlockedPath, "# что_было = на_что_не_менять" + Environment.NewLine); Open(Rules.BlockedPath); });
        Add(menu, "Открыть исключения (exceptions.txt)", () => { EnsureFile(Rules.WordsPath, "# слова, которые не трогать — по одному на строку\n"); Open(Rules.WordsPath); });
        Add(menu, "Открыть лог", () => { EnsureFile(Settings.LogPath, ""); Open(Settings.LogPath); });
        Add(menu, "Открыть папку программы", () => Open(AppContext.BaseDirectory));
        Add(menu, "Перезапустить (перечитать настройки и списки)", Restart);
        menu.Items.Add(new ToolStripSeparator());
        Add(menu, "Выход", () => { _icon.Visible = false; ExitThread(); });
        return menu;
    }

    private static ToolStripMenuItem Add(ContextMenuStrip menu, string text, Action onClick)
    {
        var mi = new ToolStripMenuItem(text);
        mi.Click += (_, _) => onClick();
        menu.Items.Add(mi);
        return mi;
    }

    private void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        Save();
    }

    private void Save()
    {
        _settings.Save();
        UpdateUi();
    }

    private void UpdateUi()
    {
        bool on = _settings.Enabled;
        _icon.Icon = on ? _iconOn : _iconOff;
        var hk = _settings.Hotkey;
        bool paused = _engine?.IsPaused == true;
        _icon.Icon = on && !paused ? _iconOn : _iconOff;
        _icon.Text = !_dicts.IsLoaded ? "Switcher — загрузка словарей…"
                   : paused ? $"Switcher — пауза до {_engine!.PausedUntil.ToLocalTime():HH:mm}"
                   : on ? $"Switcher — работает ({hk}: переключить/отменить)" : "Switcher — выключен";
        if (_miPause != null) _miPause.Text = paused ? $"Пауза (до {_engine!.PausedUntil.ToLocalTime():HH:mm})" : "Пауза";
        _miStatus.Text = _dicts.IsLoaded ? $"Switcher v{Version}" : "Switcher — загрузка словарей…";
        _miEnabled.Checked = on;
        _miSwitch.Checked = _settings.AutoSwitchLayout;
        _miSpell.Checked = _settings.AutoFixSpelling;
        _miBeep.Checked = _settings.Beep;
        _miLog.Checked = _settings.LogActions;
        _miAutostart.Checked = IsAutostart();
    }

    private static string Version => typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "1.0";

    private void Restart()
    {
        _icon.Visible = false;
        _engine?.Dispose();
        // the new instance waits for this process to exit (single-instance mutex) instead of racing it
        try { Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, Arguments = $"--wait-for {Environment.ProcessId}" }); }
        catch (Exception ex) { Log.Write("Restart failed: " + ex.Message); }
        ExitThread();
    }

    // ------------------------------------------------------------------ autostart

    public static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var v = key?.GetValue(RunName) as string;
        return v != null && v.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey)!;
        key.DeleteValue("LayoutFix", throwOnMissingValue: false); // the program's former name
        if (enable) key.SetValue(RunName, $"\"{ExePath}\"");
        else key.DeleteValue(RunName, throwOnMissingValue: false);
    }

    // ------------------------------------------------------------------ helpers

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Open failed: " + ex.Message); }
    }

    private static void EnsureFile(string path, string initial)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, initial);
        }
        catch { }
    }

    private void BeginInvokeUi(Action a)
    {
        // NotifyIcon has no handle to marshal through; use the menu strip which lives on the UI thread.
        var strip = _icon.ContextMenuStrip!;
        if (strip.IsHandleCreated) strip.BeginInvoke(a);
        else strip.HandleCreated += (_, _) => strip.BeginInvoke(a);
        if (!strip.IsHandleCreated) _ = strip.Handle; // force handle creation
    }

    /// <summary>Draw a small "Яa" badge — no external icon file needed.</summary>
    private static Icon MakeIcon(Color back)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(back);
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 7);
            g.FillPath(brush, path);
            using var font = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("Яa", font, Brushes.White, new RectangleF(0, 1, 32, 30), fmt);
        }
        var h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        return (Icon)tmp.Clone();
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine?.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
