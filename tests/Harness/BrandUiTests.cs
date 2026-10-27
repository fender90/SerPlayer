using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SerPlayer;

/// <summary>
/// Publisher / license details, plus the setup wizard pages, About, Shortcuts and default-app windows rendered
/// offscreen (shown without activation, so focus is never stolen).
/// </summary>
static class BrandUiTests
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    static void Pump(double s)
    {
        var end = DateTime.UtcNow.AddSeconds(s);
        while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); }
    }

    static void Snap(Form f, string name)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-31900, -31900);
        ShowWindow(f.Handle, 4); // SW_SHOWNOACTIVATE
        Pump(0.5);
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(Path.Combine(H.Out, name + ".png"));
        Console.WriteLine($"  saved {name}.png");
    }

    public static void Run()
    {
        // License files: embedded, and the CC BY 4.0 legal code is complete
        var asm = typeof(Installer).Assembly;
        foreach (var res in new[] { "SerPlayer.LICENSE.txt", "SerPlayer.THIRD-PARTY-NOTICES.txt", "SerPlayer.app.ico", "SerPlayer.hero.jpg" })
            H.Check(asm.GetManifestResourceStream(res) != null, $"embedded {res}");
        using (var r = new StreamReader(asm.GetManifestResourceStream("SerPlayer.LICENSE.txt")!))
        {
            string text = r.ReadToEnd();
            H.Check(text.Contains("Attribution 4.0 International") && text.Contains("Section 8 -- Interpretation."),
                "LICENSE.txt holds the full CC BY 4.0 legal code");
            H.Check(text.Contains("Vesperis Labs") && !text.Contains("Vecteezy"), "LICENSE.txt names the publisher, no third-party art");
        }

        // The installer writes them next to the exe (exercised on a scratch folder, no registry changes)
        string scratch = Path.Combine(H.Out, "install-files");
        Directory.CreateDirectory(scratch);
        var write = typeof(Installer).GetMethod("WriteResource", BindingFlags.NonPublic | BindingFlags.Static)!;
        write.Invoke(null, new object[] { "SerPlayer.LICENSE.txt", Path.Combine(scratch, AppInfo.LicenseFile) });
        write.Invoke(null, new object[] { "SerPlayer.THIRD-PARTY-NOTICES.txt", Path.Combine(scratch, AppInfo.NoticesFile) });
        H.Check(new FileInfo(Path.Combine(scratch, AppInfo.LicenseFile)).Length > 15000 &&
                new FileInfo(Path.Combine(scratch, AppInfo.NoticesFile)).Length > 1000, "installer writes LICENSE.txt and THIRD-PARTY-NOTICES.txt");

        H.Check(AppInfo.Publisher == "Vesperis Labs" && AppInfo.License == "CC BY 4.0", $"publisher {AppInfo.Publisher}, license {AppInfo.License}");

        // The start-screen "add video" glyph, large and at button size
        using (var bmp = new Bitmap(420, 280))
        using (var g = Graphics.FromImage(bmp))
        using (var white = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
        {
            g.Clear(Theme.Bg);
            AddVideoIcon.Fill(g, new RectangleF(10, 10, 256, 256), white);
            AddVideoIcon.Fill(g, new RectangleF(300, 100, 47, 47), white);
            AddVideoIcon.Fill(g, new RectangleF(370, 112, 24, 24), white);
            bmp.Save(Path.Combine(H.Out, "b7_add_video_icon.png"));
            Console.WriteLine("  saved b7_add_video_icon.png");
        }

        // Windows
        var setup = new SetupForm(uninstall: false);
        Snap(setup, "b1_setup_scope");
        setup.GetType().GetMethod("ShowDestination", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(setup, null);
        Pump(0.3);
        Snap(setup, "b2_setup_destination");
        setup.Close();

        var about = new AboutDialog();
        Snap(about, "b3_about");
        about.Close();
        var keys = new ShortcutsDialog(10);
        Snap(keys, "b4_shortcuts");
        keys.Close();
        var help = new DefaultAppDialog();
        Snap(help, "b5_default_app");
        help.Close();

        var main = new PlayerForm(null) { ClientSize = new Size(1100, 700) };
        Snap(main, "b6_main_empty");
        main.Close();
    }
}
