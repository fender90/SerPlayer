namespace SerPlayer;

/// <summary>Help window: how to make SER Player the default app for .ser files, with shortcuts to do it.</summary>
sealed class DefaultAppDialog : PagedWindow
{
    const int WinW = 680, WinH = 540, Gutter = 40, PageTop = 44, PageBottom = 28;

    public DefaultAppDialog()
        : base("Make SER Player the default for .ser files", new Size(WinW, WinH), new Rectangle(Gutter, PageTop, WinW - 2 * Gutter, WinH - PageTop - PageBottom), minimizeBox: false)
    {
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        bool isDefault = DefaultApp.IsDefault();
        AddText("Make SER Player the default for .ser files", H1, Theme.Fg);
        AddText(isDefault
                ? "✓  SER Player is already the default for .ser files."
                : "Windows only lets you change default apps yourself, so SER Player can't switch it silently. It only takes a click:",
            Body, isDefault ? Theme.Good : Theme.Dim, 6);

        AddText("QUICKEST WAY", Section, Theme.Faint, 20);
        AddText("1.  Click \"Choose default app…\" below.\n" +
                "2.  In the Windows window, select SER Player.\n" +
                "3.  Click \"Always\" (Windows 11) or tick \"Always use this app\" and click OK (Windows 10).", Body, Theme.Fg, 6);

        AddText("FROM FILE EXPLORER", Section, Theme.Faint, 18);
        AddText("1.  Right-click any .ser file and choose Open with → Choose another app.\n" +
                "2.  Select SER Player. If it isn't listed, choose \"Choose an app on your PC\" and pick:\n" +
                $"      {Installer.InstalledExe}\n" +
                "3.  Click \"Always\".", Body, Theme.Fg, 6);

        AddText("FROM SETTINGS", Section, Theme.Faint, 18);
        AddText("Settings → Apps → Default apps → type .ser in \"Set a default for a file type\" → choose SER Player.", Body, Theme.Fg, 6);

        Footer(new[] { Button("Close", PillButton.Kind.Ghost, Close) },
            Button("Open Default apps settings", PillButton.Kind.Secondary, DefaultApp.OpenSettings),
            Button("Choose default app…", PillButton.Kind.Primary, () => { if (!DefaultApp.ShowPicker(this)) DefaultApp.OpenSettings(); Close(); }));
    }
}
