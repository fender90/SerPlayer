using System.Globalization;

namespace SerPlayer;

/// <summary>Locations and helpers shared by the harness tests.</summary>
static class H
{
    // bin\Release\net8.0-windows\ → the harness folder
    static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\.."));
    public static readonly string Out = Path.Combine(Root, "out");
    public static int Fails;

    public static void Check(bool ok, string what)
    {
        if (!ok) Fails++;
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Directory.CreateDirectory(H.Out);
        foreach (var mode in args.Length == 0 ? new[] { "decode", "brand" } : args)
        {
            Console.WriteLine($"=== {mode}");
            switch (mode)
            {
                case "decode": DecodeTests.Run(); break;
                case "brand": BrandUiTests.Run(); break;
                default: Console.WriteLine($"unknown mode {mode}"); return 1;
            }
        }
        Console.WriteLine(H.Fails == 0 ? "ALL OK" : $"{H.Fails} FAILED");
        return H.Fails == 0 ? 0 : 1;
    }
}
