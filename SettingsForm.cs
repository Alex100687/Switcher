using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Switcher;

/// <summary>
/// Settings and learning window. Everything applies immediately: settings are saved on each change and the engine
/// re-reads what it needs (hotkey, pause); rule edits go straight to the rule files.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly Rules _rules;
    private readonly Engine _engine;
    private readonly Action _saved;

    // learning tab
    private ListView _list = null!;
    private ComboBox _kindFilter = null!;

    public SettingsForm(Settings settings, Rules rules, Engine engine, Action onSaved, int openTab = 0)
    {
        _settings = settings; _rules = rules; _engine = engine; _saved = onSaved;
        Text = "Switcher — настройки";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(640, 650);
        MinimumSize = new Size(560, 560);
        Font = new Font("Segoe UI", 9.5f);
        MaximizeBox = false;
        ShowInTaskbar = true;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildAppsTab());
        tabs.TabPages.Add(BuildLearningTab());
        Controls.Add(tabs);
        tabs.SelectedIndex = Math.Clamp(openTab, 0, 2);

        _rules.Changed += OnRulesChanged;
        FormClosed += (_, _) => _rules.Changed -= OnRulesChanged;
    }

    /// <summary>Re-read every control from the settings (changed from the tray menu or by editing settings.json).</summary>
    private readonly List<Action> _sync = new();
    private bool _syncing;

    public void SyncFromSettings()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(SyncFromSettings); return; }
        _syncing = true;
        try { foreach (var a in _sync) a(); }
        finally { _syncing = false; }
    }

    private void Save()
    {
        if (_syncing) return; // controls are being updated from the settings, not by the user
        _settings.Save();
        _saved();
    }

    // ------------------------------------------------------------------ tab 1: general

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("Основное") { Padding = new Padding(12) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));

        Check(grid, "Включено", () => _settings.Enabled, v => _settings.Enabled = v);
        Check(grid, "Автопереключение раскладки (ghbdtn → привет)", () => _settings.AutoSwitchLayout, v => _settings.AutoSwitchLayout = v);
        Check(grid, "Автоисправление опечаток (жызнь → жизнь)", () => _settings.AutoFixSpelling, v => _settings.AutoFixSpelling = v);
        Check(grid, "Регистр: ПОжалуйста, пРИВЕТ (Caps Lock), москва → Москва, сша → США", () => _settings.FixCase, v => _settings.FixCase = v);
        Check(grid, "Заглавная буква в начале предложения", () => _settings.CapitalizeSentences, v => _settings.CapitalizeSentences = v);
        Check(grid, "Пробелы: ка кдела → как дела, привет ,как → привет, как", () => _settings.FixSpaces, v => _settings.FixSpaces = v);
        Check(grid, "Звук при замене", () => _settings.Beep, v => _settings.Beep = v);
        Check(grid, "Записывать заменённые слова в лог", () => _settings.LogActions, v => _settings.LogActions = v);
        Check(grid, "Запускать при входе в Windows", TrayApp.IsAutostart, TrayApp.SetAutostart);
        Check(grid, "Слать Alt+Shift, если приложение игнорирует смену раскладки", () => _settings.ToggleHotkeyIfIgnored, v => _settings.ToggleHotkeyIfIgnored = v);

        // hotkey capture
        grid.Controls.Add(new Label { Text = "Горячая клавиша (переключить / отменить):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 3, 3) });
        var hk = new TextBox { Text = _settings.Hotkey, ReadOnly = true, Width = 180, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };
        var tip = new ToolTip();
        tip.SetToolTip(hk, "Кликните и нажмите нужное сочетание. Esc — вернуть Pause.");
        var hkWarn = new Label { AutoSize = true, ForeColor = Color.Firebrick, Visible = false, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 3, 3) };
        hk.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true; e.Handled = true;
            var key = e.KeyCode;
            if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
            // the hotkey is swallowed in every program: a typing key on its own would stop working everywhere
            if (key != Keys.Escape && !Hotkey.IsAllowed(key, e.Control, e.Alt, win: false))
            {
                hkWarn.Text = $"«{key}» нужна для набора текста. Выберите Pause, F-клавишу или сочетание с Ctrl/Alt.";
                hkWarn.Visible = true;
                return;
            }
            hkWarn.Visible = false;
            string combo = key == Keys.Escape ? "Pause"
                : string.Join("+", new[] { e.Control ? "Ctrl" : null, e.Alt ? "Alt" : null, e.Shift ? "Shift" : null, key.ToString() }.Where(x => x != null));
            hk.Text = combo; _settings.Hotkey = combo; _engine.ReloadHotkey(); Save();
        };
        grid.Controls.Add(hk);
        grid.Controls.Add(hkWarn);
        grid.SetColumnSpan(hkWarn, 2);
        _sync.Add(() => { hk.Text = _settings.Hotkey; hkWarn.Visible = false; });

        Number(grid, "Минимум букв для переключения раскладки:", 1, 6, () => _settings.MinWordLength, v => _settings.MinWordLength = v);
        Number(grid, "Минимум букв для исправления опечаток:", 2, 8, () => _settings.MinSpellFixLength, v => _settings.MinSpellFixLength = v);

        // pause
        grid.Controls.Add(new Label { Text = "Пауза:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 10, 3, 3) });
        var pausePanel = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) };
        var pauseLabel = new Label { AutoSize = true, Margin = new Padding(6, 6, 3, 3), ForeColor = Color.DimGray };
        void RefreshPause() => pauseLabel.Text = _engine.IsPaused ? $"до {_engine.PausedUntil.ToLocalTime():HH:mm}" : "нет";
        foreach (var (text, minutes) in new[] { ("15 мин", 15), ("1 час", 60), ("Возобновить", 0) })
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (_, _) => { _engine.PausedUntil = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : DateTime.MinValue; RefreshPause(); _saved(); };
            pausePanel.Controls.Add(b);
        }
        pausePanel.Controls.Add(pauseLabel);
        RefreshPause();
        grid.Controls.Add(pausePanel);

        page.Controls.Add(grid);
        var note = new Label
        {
            Dock = DockStyle.Bottom, AutoSize = false, Height = 48, ForeColor = Color.DimGray,
            Text = "Всё применяется сразу. Файлы данных и лог: " + Settings.Dir,
        };
        page.Controls.Add(note);
        return page;
    }

    private void Check(TableLayoutPanel grid, string text, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Text = text, AutoSize = true, Checked = get(), Anchor = AnchorStyles.Left };
        cb.CheckedChanged += (_, _) => { if (_syncing) return; set(cb.Checked); Save(); };
        grid.Controls.Add(cb);
        grid.SetColumnSpan(cb, 2);
        _sync.Add(() => cb.Checked = get());
    }

    private void Number(TableLayoutPanel grid, string text, int min, int max, Func<int> get, Action<int> set)
    {
        grid.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) });
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(get(), min, max), Width = 60, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
        n.ValueChanged += (_, _) => { if (_syncing) return; set((int)n.Value); Save(); };
        grid.Controls.Add(n);
        _sync.Add(() => n.Value = Math.Clamp(get(), min, max));
    }

    // ------------------------------------------------------------------ tab 2: excluded apps

    private TabPage BuildAppsTab()
    {
        var page = new TabPage("Приложения") { Padding = new Padding(12) };
        var label = new Label { Dock = DockStyle.Top, Height = 40, Text = "В этих программах Switcher ничего не делает (имя процесса без .exe). По умолчанию — IDE, терминалы, 3D/графика (3ds Max, Maya, Photoshop…), Unity, Blender, RDP." };
        var list = new ListBox { Dock = DockStyle.Fill, Sorted = true };
        void Fill() { list.Items.Clear(); foreach (var p in _settings.ExcludedProcesses) list.Items.Add(p); }
        Fill();
        _sync.Add(Fill);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight };

        var add = new Button { Text = "Добавить…", AutoSize = true };
        add.Click += (_, _) =>
        {
            var name = Prompt("Имя процесса (без .exe), например Telegram:");
            if (!string.IsNullOrWhiteSpace(name)) { list.Items.Add(name.Trim()); Persist(); }
        };
        var running = new Button { Text = "Из запущенных…", AutoSize = true };
        running.Click += (_, _) =>
        {
            var names = Process.GetProcesses().Where(p => p.MainWindowHandle != IntPtr.Zero).Select(p => p.ProcessName).Distinct().OrderBy(x => x).ToArray();
            var pick = PickOne("Программы с окнами:", names);
            if (pick != null && !list.Items.Contains(pick)) { list.Items.Add(pick); Persist(); }
        };
        var remove = new Button { Text = "Удалить", AutoSize = true };
        remove.Click += (_, _) => { if (list.SelectedItem != null) { list.Items.Remove(list.SelectedItem); Persist(); } };
        buttons.Controls.AddRange(new Control[] { add, running, remove });

        void Persist()
        {
            _settings.ExcludedProcesses = list.Items.Cast<string>().ToList();
            Save();
        }

        page.Controls.Add(list);
        page.Controls.Add(buttons);
        page.Controls.Add(label);
        return page;
    }

    // ------------------------------------------------------------------ tab 3: learning

    private TabPage BuildLearningTab()
    {
        var page = new TabPage("Обучение") { Padding = new Padding(12) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34 };
        top.Controls.Add(new Label { Text = "Показать:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        _kindFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        _kindFilter.Items.AddRange(new object[] { "Всё моё", "Личные слова", "Отклонённые замены", "Автозамены", "Встроенные списки" });
        _kindFilter.SelectedIndex = 0;
        _kindFilter.SelectedIndexChanged += (_, _) => FillList();
        top.Controls.Add(_kindFilter);

        _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false };
        _list.Columns.Add("Тип", 130);
        _list.Columns.Add("Правило", 300);
        _list.Columns.Add("Где", 120);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40 };
        var addWord = new Button { Text = "Личное слово…", AutoSize = true };
        addWord.Click += (_, _) => { var w = Prompt("Слово, которое не трогать:"); if (!string.IsNullOrWhiteSpace(w)) _rules.Add(new Rule(RuleKind.Word, w.Trim(), "", RuleScope.Global, false)); };
        var addAuto = new Button { Text = "Автозамена…", AutoSize = true };
        addAuto.Click += (_, _) =>
        {
            var from = Prompt("Что набрано:"); if (string.IsNullOrWhiteSpace(from)) return;
            var to = Prompt($"На что заменять «{from.Trim()}»:"); if (string.IsNullOrWhiteSpace(to)) return;
            _rules.Add(new Rule(RuleKind.Autocorrect, from.Trim(), to.Trim(), RuleScope.Global, false));
        };
        var scope = new Button { Text = "Где действует…", AutoSize = true };
        scope.Click += (_, _) =>
        {
            var rules = Selected().Where(r => !r.Builtin).ToList();
            if (rules.Count == 0) return;
            var names = Process.GetProcesses().Where(p => p.MainWindowHandle != IntPtr.Zero).Select(p => p.ProcessName).Distinct().OrderBy(x => x).ToList();
            names.Insert(0, "Везде");
            var pick = PickOne("Правило действует:", names.ToArray());
            if (pick == null) return;
            foreach (var r in rules) _rules.SetScope(r, pick == "Везде" ? RuleScope.Global : pick);
        };
        var del = new Button { Text = "Удалить", AutoSize = true };
        del.Click += (_, _) => { foreach (var r in Selected().Where(r => !r.Builtin)) _rules.Remove(r); };
        var forget = new Button { Text = "Забыть всё обучение", AutoSize = true, ForeColor = Color.Firebrick };
        forget.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Удалить все личные слова, отклонённые замены и свои автозамены? Встроенные списки останутся.",
                    "Switcher", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                _rules.ForgetAll();
        };
        buttons.Controls.AddRange(new Control[] { addWord, addAuto, scope, del, forget });

        var hint = new Label
        {
            Dock = DockStyle.Bottom, Height = 44, ForeColor = Color.DimGray,
            Text = "Откат через Pause запоминает отклонённую замену. После трёх отмен одного слова программа предложит добавить его в личные слова. Встроенные списки не редактируются.",
        };

        page.Controls.Add(_list);
        page.Controls.Add(top);
        page.Controls.Add(hint);
        page.Controls.Add(buttons);
        FillList();
        return page;
    }

    private IEnumerable<Rule> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(i => (Rule)i.Tag!);

    private void OnRulesChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(FillList); else FillList();
    }

    private void FillList()
    {
        int f = _kindFilter.SelectedIndex;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _rules.Snapshot())
        {
            bool show = f switch
            {
                0 => !r.Builtin,
                1 => !r.Builtin && r.Kind == RuleKind.Word,
                2 => !r.Builtin && r.Kind == RuleKind.Blocked,
                3 => !r.Builtin && r.Kind == RuleKind.Autocorrect,
                _ => r.Builtin,
            };
            if (!show) continue;
            string kind = r.Kind switch { RuleKind.Word => "Личное слово", RuleKind.Blocked => "Не заменять", _ => "Автозамена" };
            var item = new ListViewItem(new[] { kind + (r.Builtin ? " (встроенное)" : ""), r.Display, r.Scope == RuleScope.Global ? "везде" : r.Scope }) { Tag = r };
            if (r.Builtin) item.ForeColor = Color.Gray;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    // ------------------------------------------------------------------ tiny dialogs

    private string? Prompt(string caption)
    {
        using var f = new Form { Text = "Switcher", Size = new Size(420, 150), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, Font = Font };
        var label = new Label { Text = caption, Left = 12, Top = 12, Width = 380, AutoSize = false };
        var box = new TextBox { Left = 12, Top = 40, Width = 380 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 236, Top = 74, Width = 75 };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 317, Top = 74, Width = 75 };
        f.Controls.AddRange(new Control[] { label, box, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    private string? PickOne(string caption, string[] items)
    {
        using var f = new Form { Text = "Switcher", Size = new Size(420, 420), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, Font = Font };
        var label = new Label { Text = caption, Dock = DockStyle.Top, Height = 28, Padding = new Padding(8, 6, 0, 0) };
        var list = new ListBox { Dock = DockStyle.Fill };
        list.Items.AddRange(items);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 75 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 75 };
        panel.Controls.AddRange(new Control[] { cancel, ok });
        list.DoubleClick += (_, _) => { if (list.SelectedItem != null) f.DialogResult = DialogResult.OK; };
        f.Controls.AddRange(new Control[] { list, label, panel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? list.SelectedItem as string : null;
    }
}
