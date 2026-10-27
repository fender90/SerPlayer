using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SerPlayer;

/// <summary>
/// Install / update / uninstall wizard in the player's design language: borderless dark window with rounded corners,
/// the app artwork as a side panel, option cards, pill buttons, toggle switches and a thin progress bar.
/// Flow: installation type (me only / all users) → destination & shortcuts → progress → finished.
/// </summary>
sealed class SetupForm : Form
{
    const int WinW = 820, WinH = 520, HeroW = 300, Gutter = 44, PageTop = 64, PageBottom = 32;

    readonly Image? _hero;
    readonly PassThroughPanel _page;
    readonly ChromeButton _min, _close;
    readonly Font _h1 = new("Segoe UI Semibold", 20f), _body = new("Segoe UI", 10.5f), _small = new("Segoe UI", 9f),
        _section = new("Segoe UI Semibold", 8.5f), _heroTitle = new("Segoe UI Semibold", 22f), _heroSub = new("Segoe UI", 10f),
        _heroBy = new("Segoe UI Semibold", 10.5f);
    readonly InstallScope? _uninstallScope;
    bool _working;
    int _y;

    InstallScope _scope;
    PathBox? _path;
    ToggleSwitch? _startMenu, _desktop, _launch;
    PassLabel? _inlineError, _step, _percent;
    ThinProgress? _bar;

    /// <param name="uninstall">Start on the uninstall flow (Settings → Apps).</param>
    /// <param name="uninstallScope">Which installation Settings asked to remove; null = whatever is installed.</param>
    public SetupForm(bool uninstall, InstallScope? uninstallScope = null)
    {
        Text = uninstall ? "Uninstall SER Player" : "SER Player Setup";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        DoubleBuffered = true;
        KeyPreview = true;
        ClientSize = new Size(P(WinW), P(WinH));
        _uninstallScope = uninstallScope;

        using (var s = typeof(SetupForm).Assembly.GetManifestResourceStream("SerPlayer.hero.jpg"))
            if (s != null) _hero = Image.FromStream(s);

        _page = new PassThroughPanel
        {
            BackColor = Theme.Bg,
            Bounds = new Rectangle(P(HeroW + Gutter), P(PageTop), P(WinW - HeroW - 2 * Gutter), P(WinH - PageTop - PageBottom)),
        };
        Controls.Add(_page);

        int bw = P(44), bh = P(32), top = P(12);
        _close = new ChromeButton(ChromeButton.Kind.Close) { Bounds = new Rectangle(ClientSize.Width - P(12) - bw, top, bw, bh) };
        _min = new ChromeButton(ChromeButton.Kind.Minimize) { Bounds = new Rectangle(_close.Left - bw - P(2), top, bw, bh) };
        _close.Click += (_, _) => TryClose();
        _min.Click += (_, _) => WindowState = FormWindowState.Minimized;
        Controls.Add(_min);
        Controls.Add(_close);

        // Default to the scope that's already installed (so an update stays where it is), else "me only".
        _scope = Installer.InstalledDirectory(InstallScope.CurrentUser) == null && Installer.InstalledDirectory(InstallScope.AllUsers) != null
            ? InstallScope.AllUsers : InstallScope.CurrentUser;

        // Publisher's website under "by Vesperis Labs" in the art panel (a real link: the rest of the window drags it)
        var site = new LinkLabel
        {
            Text = AppInfo.WebsiteLabel, Font = _heroSub, AutoSize = true, BackColor = Theme.Bg,
            LinkColor = Theme.AccentHover, ActiveLinkColor = Color.White, VisitedLinkColor = Theme.AccentHover,
            LinkBehavior = LinkBehavior.HoverUnderline, Location = new Point(P(31), ClientSize.Height - P(72)),
        };
        site.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(AppInfo.Website) { UseShellExecute = true }); }
            catch (Exception ex) { CrashLog.Write(ex, "Opening the website"); }
        };
        Controls.Add(site);

        if (uninstall) ShowUninstallConfirm(fromSetup: false); else ShowScope();
    }

    int P(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    static string ScopeName(InstallScope s) => s == InstallScope.AllUsers ? "all users" : "you only";

    // ───────────────────────── window chrome ─────────────────────────

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000;        // WS_MINIMIZEBOX: minimise from the taskbar
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, dark = 1;
        try
        {
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); // DWMWCP_ROUND (Windows 11)
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));  // dark mode
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDBLCLK = 0xA3, HTCLIENT = 1, HTCAPTION = 2;
        if (m.Msg == WM_NCLBUTTONDBLCLK) return; // no maximise on double-click
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && m.Result == HTCLIENT) m.Result = HTCAPTION; // drag from anywhere that isn't a control
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { TryClose(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void TryClose()
    {
        if (!_working) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_working) e.Cancel = true; // never interrupt a half-done install
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        var hero = new Rectangle(0, 0, P(HeroW), ClientSize.Height);
        // Square artwork, sized so the whole player circle fits the panel width; it sits at the top and fades out
        // into the background, leaving the bottom for the title.
        int art = P(440);
        var artRect = new Rectangle((hero.Width - art) / 2, 0, art, art);
        if (_hero != null)
        {
            var st = g.Save();
            g.SetClip(hero);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_hero, artRect);
            g.Restore(st);
        }
        // Brush rects are 1 px larger than the fill so GDI+ never wraps the gradient (which leaves a seam line).
        var fade = new Rectangle(0, artRect.Bottom - P(170), hero.Width, P(170) + 1);
        using (var lg = new LinearGradientBrush(Rectangle.Inflate(fade, 0, 1), Color.FromArgb(0, Theme.Bg), Theme.Bg, 90f))
            g.FillRectangle(lg, fade);
        using (var below = new SolidBrush(Theme.Bg)) g.FillRectangle(below, 0, fade.Bottom, hero.Width, hero.Height - fade.Bottom);
        var edge = new Rectangle(hero.Right - P(70), 0, P(70) + 1, hero.Height);
        using (var lg = new LinearGradientBrush(Rectangle.Inflate(edge, 1, 0), Color.FromArgb(0, Theme.Bg), Theme.Bg, 0f))
            g.FillRectangle(lg, edge);

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var white = new SolidBrush(Color.White);
        using var dim = new SolidBrush(Theme.Dim);
        using var fg = new SolidBrush(Theme.Fg);
        g.DrawString(AppInfo.Name, _heroTitle, white, P(30), hero.Height - P(134));
        g.DrawString($"by {AppInfo.Publisher}", _heroBy, fg, P(32), hero.Height - P(92));
        // the website link (a LinkLabel) sits at hero.Height - 72
        g.DrawString($"Version {Installer.Version}", _heroSub, dim, P(32), hero.Height - P(42));
    }

    // ───────────────────────── page building ─────────────────────────

    void NewPage()
    {
        foreach (var c in _page.Controls.Cast<Control>().ToArray()) c.Dispose();
        _page.Controls.Clear();
        _y = 0;
        AcceptButton = null;
        _inlineError = _step = _percent = null;
        _bar = null;
    }

    PassLabel AddText(string text, Font font, Color color, int gapBefore = 0, int? fixedHeight = null)
    {
        _y += P(gapBefore);
        var size = TextRenderer.MeasureText(text.Length == 0 ? " " : text, font, new Size(_page.Width, int.MaxValue), TextFormatFlags.WordBreak);
        var l = new PassLabel
        {
            Text = text, Font = font, ForeColor = color, BackColor = Theme.Bg, AutoSize = false,
            Bounds = new Rectangle(0, _y, _page.Width, fixedHeight ?? size.Height),
        };
        _page.Controls.Add(l);
        _y += l.Height;
        return l;
    }

    T AddControl<T>(T c, int height, int gapBefore = 0, int? width = null) where T : Control
    {
        _y += P(gapBefore);
        c.Bounds = new Rectangle(0, _y, width ?? _page.Width, height);
        _page.Controls.Add(c);
        _y += height;
        return c;
    }

    void AddFootnote(string text)
    {
        _page.Controls.Add(new PassLabel
        {
            Text = text, Font = _small, ForeColor = Theme.Faint, BackColor = Theme.Bg, AutoSize = false,
            // room for two lines, bottom-aligned just above the buttons
            Bounds = new Rectangle(0, _page.Height - P(38) - P(54), _page.Width, P(40)),
            TextAlign = ContentAlignment.BottomLeft,
        });
    }

    /// <summary>
    /// Bottom button row. <paramref name="left"/> buttons sit at the left edge (Cancel, Uninstall); <paramref name="right"/>
    /// ones are right-aligned together (Back next to Next/Install). Both are given left to right; the last right one is default.
    /// </summary>
    void Footer(PillButton[]? left, params PillButton[] right)
    {
        int y = _page.Height - P(38), x = _page.Width;
        foreach (var b in right.Reverse())
        {
            var s = b.GetPreferredSize(Size.Empty);
            x -= s.Width;
            b.Bounds = new Rectangle(x, y, s.Width, s.Height);
            _page.Controls.Add(b);
            x -= P(10);
        }
        int lx = 0;
        foreach (var b in left ?? Array.Empty<PillButton>())
        {
            var s = b.GetPreferredSize(Size.Empty);
            b.Bounds = new Rectangle(lx, y, s.Width, s.Height);
            _page.Controls.Add(b);
            lx += s.Width + P(4);
        }
        if (right.Length > 0) { AcceptButton = right[^1]; right[^1].Focus(); }
    }

    static PillButton Button(string text, PillButton.Kind kind, Action onClick, bool shield = false)
    {
        var b = new PillButton(text, kind) { Shield = shield };
        b.Click += (_, _) => onClick();
        return b;
    }

    void ShowInlineError(string message)
    {
        if (_inlineError == null) return;
        _inlineError.Text = message;
        _inlineError.Visible = true;
    }

    // ───────────────────────── 1. installation type ─────────────────────────

    void ShowScope()
    {
        NewPage();
        var installed = Installer.InstalledScopes().ToList();
        AddText("Choose installation type", _h1, Theme.Fg);
        AddText(installed.Count == 0
                ? "Install SER Player just for your account, or for everyone who uses this PC."
                : $"SER Player {Installer.InstalledVersion(installed[0])} is installed for {ScopeName(installed[0])}." +
                  (installed.Count > 1 ? " It's also installed for you only." : ""),
            _body, Theme.Dim, 6);

        var me = AddControl(new OptionCard("Install for me only",
            "Installs in your user profile. No administrator permission needed."), P(84), 22);
        var all = AddControl(new OptionCard("Install for all users",
            "Installs in Program Files for everyone on this PC. Requires administrator permission."), P(84), 12);
        me.Selected = _scope == InstallScope.CurrentUser;
        all.Selected = _scope == InstallScope.AllUsers;
        me.SelectedChanged += (_, _) => { if (me.Selected) { all.Selected = false; _scope = InstallScope.CurrentUser; } };
        all.SelectedChanged += (_, _) => { if (all.Selected) { me.Selected = false; _scope = InstallScope.AllUsers; } };

        AddFootnote($"{AppInfo.Name} is free under the {AppInfo.License} license.\n{AppInfo.Copyright}  ·  {AppInfo.WebsiteLabel}");

        var left = new List<PillButton> { Button("Cancel", PillButton.Kind.Ghost, TryClose) };
        if (installed.Count > 0) left.Add(Button("Uninstall", PillButton.Kind.Ghost, () => ShowUninstallConfirm(fromSetup: true)));
        Footer(left.ToArray(), Button("Next", PillButton.Kind.Primary, ShowDestination));
    }

    // ───────────────────────── 2. destination & shortcuts ─────────────────────────

    static int CompareVersions(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va.CompareTo(vb) : string.CompareOrdinal(a, b);

    InstallScope OtherScope => _scope == InstallScope.AllUsers ? InstallScope.CurrentUser : InstallScope.AllUsers;

    void ShowDestination()
    {
        NewPage();
        var scope = _scope;
        string? installed = Installer.InstalledVersion(scope);
        string? installedDir = Installer.InstalledDirectory(scope);
        int cmp = installed == null ? 1 : CompareVersions(Installer.Version, installed);
        string verb = installed == null ? "Install" : cmp > 0 ? "Update" : cmp == 0 ? "Reinstall" : "Install";
        bool needsAdmin = scope == InstallScope.AllUsers && !Installer.IsElevated;

        AddText($"{verb} SER Player", _h1, Theme.Fg);
        string who = scope == InstallScope.AllUsers ? "For everyone on this PC." : "For your account only.";
        AddText(installed == null
                ? $"{who} Choose where to put it and which shortcuts you'd like."
                : cmp > 0 ? $"{who} Version {installed} is installed; this updates it to {Installer.Version}."
                : cmp == 0 ? $"{who} Version {installed} is already installed here; you can reinstall it."
                : $"{who} A newer version ({installed}) is installed; continuing replaces it with {Installer.Version}.",
            _body, Theme.Dim, 6);

        AddText("INSTALL LOCATION", _section, Theme.Faint, 22);
        _y += P(8);
        var browse = new PillButton("Browse…");
        int bw = browse.GetPreferredSize(Size.Empty).Width, rowH = P(40);
        _path = new PathBox { Bounds = new Rectangle(0, _y, _page.Width - bw - P(10), rowH) };
        _path.Text = installedDir ?? Installer.DefaultDirectory(scope);
        _path.TextChanged += (_, _) => { if (_inlineError != null) _inlineError.Visible = false; };
        browse.Bounds = new Rectangle(_page.Width - bw, _y + (rowH - P(38)) / 2, bw, P(38));
        browse.Click += (_, _) => BrowseForFolder();
        _page.Controls.Add(_path);
        _page.Controls.Add(browse);
        _y += rowH;
        _inlineError = AddText("", _small, Theme.Danger, 6, fixedHeight: P(34));
        _inlineError.Visible = false;

        bool fresh = installed == null;
        _startMenu = AddControl(new ToggleSwitch("Start menu shortcut", fresh || Installer.HasStartMenuShortcut(scope),
            scope == InstallScope.AllUsers ? "In everyone's Start menu" : "Find SER Player from the Start menu"), P(46), 0);
        _desktop = AddControl(new ToggleSwitch("Desktop shortcut", Installer.HasDesktopShortcut(scope),
            scope == InstallScope.AllUsers ? "On everyone's desktop" : "Put an icon on your desktop"), P(46), 12);

        if (Installer.InstalledDirectory(OtherScope) != null)
            AddFootnote($"The copy installed for {ScopeName(OtherScope)} will be removed. Your default app choice is kept.");

        Footer(new[] { Button("Cancel", PillButton.Kind.Ghost, TryClose) },
            Button("Back", PillButton.Kind.Secondary, ShowScope),
            Button(verb, PillButton.Kind.Primary, () => StartInstall(verb), shield: needsAdmin));
    }

    void BrowseForFolder()
    {
        if (_path == null) return;
        string current = _path.Text.Trim();
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose where to install SER Player",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(current) ? current : Path.GetDirectoryName(current) ?? "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string dir = dlg.SelectedPath.TrimEnd('\\');
        // Keep the app in its own folder unless the user already picked one named for it.
        string name = Path.GetFileName(dir);
        if (!name.Equals("SerPlayer", StringComparison.OrdinalIgnoreCase) && !name.Equals("SER Player", StringComparison.OrdinalIgnoreCase))
            dir = Path.Combine(dir, _scope == InstallScope.AllUsers ? "SER Player" : "SerPlayer");
        _path.Text = dir;
    }

    void StartInstall(string verb)
    {
        if (_path == null || _startMenu == null || _desktop == null) return;
        var scope = _scope;
        string dir = _path.Text.Trim().Trim('"').TrimEnd('\\');
        if (dir.Length == 0) { ShowInlineError("Choose a folder to install into."); return; }
        if (Installer.CheckDirectory(dir, scope) is string problem) { ShowInlineError(problem); return; }
        if (Installer.IsRunningFrom(dir) || Installer.InstalledScopes().Any(s => Installer.IsRunningFrom(Installer.InstalledDirectory(s)!)))
        {
            ShowInlineError("SER Player is open. Close it, then try again.");
            return;
        }
        var options = new InstallOptions(scope, Path.GetFullPath(dir), _startMenu.Checked, _desktop.Checked);
        bool updating = verb == "Update";
        bool moveFromOther = Installer.InstalledDirectory(OtherScope) != null;
        var other = OtherScope;
        ShowProgress(updating ? "Updating SER Player" : "Installing SER Player");
        Run(report =>
        {
            // Scale the steps so moving away from the other scope gets the last part of the bar.
            Action<string, double> main = moveFromOther ? (s, f) => report(s, f * 0.85) : report;
            if (scope == InstallScope.AllUsers && !Installer.IsElevated) ElevatedHelper.InstallAllUsers(options, main);
            else Installer.Install(options, main);

            if (moveFromOther)
            {
                report($"Removing the copy installed for {ScopeName(other)}…", 0.88);
                if (other == InstallScope.CurrentUser)
                {
                    Installer.Uninstall(InstallScope.CurrentUser, forgetDefault: false);
                    Installer.RetargetBrowseAssociation(options.Directory);
                }
                else if (Installer.IsElevated) Installer.Uninstall(InstallScope.AllUsers, forgetDefault: false);
                else ElevatedHelper.UninstallAllUsers(forgetDefault: false, (s, f) => report(s, 0.88 + f * 0.1));
                report("Done", 1.0);
            }
        }, () => ShowFinished(updating), ShowDestination);
    }

    // ───────────────────────── 3. progress / result ─────────────────────────

    void ShowProgress(string title)
    {
        NewPage();
        AddText(title, _h1, Theme.Fg);
        _step = AddText("Preparing…", _body, Theme.Dim, 10);
        _bar = AddControl(new ThinProgress(), P(16), 30);
        _percent = AddText("0%", _small, Theme.Faint, 8);
    }

    void Report(string step, double fraction)
    {
        if (_step == null || _bar == null || _percent == null) return;
        _step.Text = step;
        _bar.Value = fraction;
        _percent.Text = $"{fraction * 100:0}%";
    }

    /// <summary>Runs install/uninstall work on a background STA thread (shortcut creation uses COM), reporting progress.</summary>
    void Run(Action<Action<string, double>> work, Action onSuccess, Action onRetry)
    {
        _working = true;
        _close.Enabled = false;
        var thread = new Thread(() =>
        {
            Exception? error = null;
            var sw = Stopwatch.StartNew();
            try { work((s, f) => BeginInvoke(() => Report(s, f))); }
            catch (Exception ex) { error = ex; }
            // Keep the progress page up long enough to read, and let the bar finish easing to 100%.
            if (error == null) Thread.Sleep((int)Math.Max(450, 1100 - sw.ElapsedMilliseconds));
            BeginInvoke(() =>
            {
                _working = false;
                _close.Enabled = true;
                if (error == null) onSuccess();
                else if (error is OperationCanceledException) ShowFailed("Administrator permission needed", error.Message, onRetry, logged: false);
                else
                {
                    CrashLog.Write(error, "Setup");
                    ShowFailed("Something went wrong", error.Message, onRetry, logged: true);
                }
            });
        }) { IsBackground = true, Name = "Setup worker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    void ShowFinished(bool updated)
    {
        NewPage();
        AddControl(new ResultBadge { Success = true }, P(68), 0, width: P(68));
        AddText(updated ? "SER Player is up to date" : "SER Player is ready", _h1, Theme.Fg, 18);
        AddText(_scope == InstallScope.AllUsers
                ? "Everyone on this PC can open it from the Start menu, or right-click a .ser file and choose Open with → SER Player."
                : "Open it from the Start menu, or right-click any .ser file and choose Open with → SER Player.",
            _body, Theme.Dim, 6);

        if (DefaultApp.IsDefault())
            AddText("✓  .ser files open with SER Player by default", _body, Theme.Good, 22);
        else
        {
            _y += P(20);
            var make = new PillButton("Make it the default for .ser files…");
            var s = make.GetPreferredSize(Size.Empty);
            make.Bounds = new Rectangle(0, _y, s.Width, s.Height);
            make.Click += (_, _) =>
            {
                if (!DefaultApp.ShowPicker(this)) DefaultApp.OpenSettings();
                if (DefaultApp.IsDefault()) { make.Text = "✓  Default for .ser files"; make.Enabled = false; }
            };
            _page.Controls.Add(make);
            _y += s.Height;
        }

        _launch = AddControl(new ToggleSwitch("Launch SER Player", true), P(28), 22);
        Footer(null, Button("Finish", PillButton.Kind.Primary, () =>
        {
            if (_launch.Checked) LaunchPlayer();
            Close();
        }));
    }

    /// <summary>Starts the installed player as the signed-in user (never elevated, even if setup itself was run as admin).</summary>
    static void LaunchPlayer()
    {
        string exe = Installer.InstalledExe;
        try
        {
            if (Installer.IsElevated) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) { CrashLog.Write(ex, "Launching after setup"); }
    }

    void ShowFailed(string title, string message, Action retry, bool logged)
    {
        NewPage();
        AddControl(new ResultBadge { Success = false }, P(68), 0, width: P(68));
        AddText(title, _h1, Theme.Fg, 18);
        AddText(message, _body, Theme.Dim, 6);
        if (logged) AddText($"Details were saved to {CrashLog.FilePath}", _small, Theme.Faint, 12);
        Footer(null, Button("Close", PillButton.Kind.Secondary, TryClose), Button("Try again", PillButton.Kind.Primary, retry));
    }

    // ───────────────────────── uninstall ─────────────────────────

    /// <summary>Installations to remove: the one Settings asked for, else every installed one.</summary>
    List<InstallScope> UninstallTargets() =>
        _uninstallScope is InstallScope s ? Installer.InstalledScopes().Where(x => x == s).ToList() : Installer.InstalledScopes().ToList();

    void ShowUninstallConfirm(bool fromSetup)
    {
        NewPage();
        var targets = UninstallTargets();
        if (targets.Count == 0)
        {
            AddText("SER Player isn't installed", _h1, Theme.Fg);
            AddText("There's nothing to remove.", _body, Theme.Dim, 6);
            Footer(null, Button("Close", PillButton.Kind.Primary, TryClose));
            return;
        }
        AddText("Uninstall SER Player", _h1, Theme.Fg);
        AddText("This removes SER Player, its shortcuts and its .ser file registration, and it stops being the default " +
                "app for .ser files. Your .ser videos are not touched.", _body, Theme.Dim, 6);
        foreach (var s in targets)
        {
            AddText(s == InstallScope.AllUsers ? "INSTALLED FOR ALL USERS" : "INSTALLED FOR YOU ONLY", _section, Theme.Faint, 20);
            AddText(Installer.InstalledDirectory(s)!, _body, Theme.Fg, 6);
        }
        _inlineError = AddText("", _small, Theme.Danger, 10, fixedHeight: P(34));
        _inlineError.Visible = false;
        bool needsAdmin = targets.Contains(InstallScope.AllUsers) && !Installer.IsElevated;
        if (needsAdmin) AddFootnote("Removing the all-users copy needs administrator permission.");

        var uninstall = Button("Uninstall", PillButton.Kind.Danger, () => StartUninstall(fromSetup), shield: needsAdmin);
        var cancel = new[] { Button("Cancel", PillButton.Kind.Ghost, TryClose) };
        if (fromSetup) Footer(cancel, Button("Back", PillButton.Kind.Secondary, ShowScope), uninstall);
        else Footer(cancel, uninstall);
    }

    void StartUninstall(bool fromSetup)
    {
        var targets = UninstallTargets();
        if (targets.Any(s => Installer.IsRunningFrom(Installer.InstalledDirectory(s)!)))
        {
            ShowInlineError("SER Player is open. Close it, then click Uninstall again.");
            return;
        }
        ShowProgress("Removing SER Player");
        Run(report =>
        {
            for (int i = 0; i < targets.Count; i++)
            {
                double from = (double)i / targets.Count, span = 1.0 / targets.Count;
                Action<string, double> part = (s, f) => report(s, from + f * span);
                if (targets[i] == InstallScope.CurrentUser) Installer.Uninstall(InstallScope.CurrentUser, forgetDefault: true, part);
                else
                {
                    if (Installer.IsElevated) Installer.Uninstall(InstallScope.AllUsers, forgetDefault: true, part);
                    else ElevatedHelper.UninstallAllUsers(forgetDefault: true, part);
                    Installer.ForgetDefaultForCurrentUser(); // the elevated helper may have run as another account
                }
            }
        }, ShowUninstalled, () => ShowUninstallConfirm(fromSetup));
    }

    void ShowUninstalled()
    {
        NewPage();
        AddControl(new ResultBadge { Success = true }, P(68), 0, width: P(68));
        AddText("SER Player has been removed", _h1, Theme.Fg, 18);
        AddText("Its shortcuts and file registration are gone, and Windows will ask which app to use for .ser files. " +
                "Your .ser files were left untouched.", _body, Theme.Dim, 6);
        Footer(null, Button("Close", PillButton.Kind.Primary, TryClose));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hero?.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
