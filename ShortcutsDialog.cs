namespace SerPlayer;

/// <summary>Keyboard and mouse shortcuts in two columns, in the app's borderless window style.</summary>
sealed class ShortcutsDialog : PagedWindow
{
    const int WinW = 780, WinH = 400, Gutter = 40, PageTop = 44, PageBottom = 28, Column = 340, KeyW = 176;

    readonly Font _key = new("Segoe UI Semibold", 9.5f), _action = new("Segoe UI", 9.5f);

    public ShortcutsDialog(int skipSeconds)
        : base($"{AppInfo.Name} — shortcuts", new Size(WinW, WinH), new Rectangle(Gutter, PageTop, WinW - 2 * Gutter, WinH - PageTop - PageBottom), minimizeBox: false)
    {
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var left = new (string Title, (string Keys, string Action)[] Rows)[]
        {
            ("PLAYBACK", new[]
            {
                ("Space", "Play / pause"),
                ("← / →, mouse wheel", "Previous / next frame"),
                ("PgUp / PgDn, J / L", $"Back / forward {skipSeconds} s"),
                ("Home / End", "First / last frame"),
            }),
            ("FILE", new[]
            {
                ("Ctrl + O", "Open a file"),
                ("B", "Swap 16-bit byte order"),
            }),
        };
        var right = new (string Title, (string Keys, string Action)[] Rows)[]
        {
            ("VIEW", new[]
            {
                ("Ctrl + / Ctrl −", "Zoom in / out"),
                ("Ctrl + wheel, pinch", "Zoom at the cursor"),
                ("Drag the video", "Pan when zoomed"),
                ("Ctrl + 0", "Reset zoom"),
                ("↑ / ↓", "Brightness"),
                ("F, F11, double-click", "Fullscreen"),
                ("Esc", "Leave fullscreen"),
            }),
        };

        AddText("Keyboard & mouse shortcuts", H1, Theme.Fg);
        int top = Y + P(14);
        PlaceGroups(left, 0, top);
        PlaceGroups(right, P(Column + 20), top);
        Footer(null, Button("Close", PillButton.Kind.Primary, Close));
    }

    void PlaceGroups((string Title, (string Keys, string Action)[] Rows)[] groups, int x, int y)
    {
        foreach (var (title, rows) in groups)
        {
            Page.Controls.Add(new PassLabel { Text = title, Font = Section, ForeColor = Theme.Faint, BackColor = Theme.Bg, AutoSize = true, Location = new Point(x, y) });
            y += P(24);
            foreach (var (keys, action) in rows)
            {
                Page.Controls.Add(new PassLabel { Text = keys, Font = _key, ForeColor = Theme.Fg, BackColor = Theme.Bg, AutoSize = true, Location = new Point(x, y) });
                Page.Controls.Add(new PassLabel { Text = action, Font = _action, ForeColor = Theme.Dim, BackColor = Theme.Bg, AutoSize = true, Location = new Point(x + P(KeyW), y) });
                y += P(23);
            }
            y += P(16);
        }
    }
}
