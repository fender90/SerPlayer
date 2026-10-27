using System.Drawing.Imaging;
using System.Text;

namespace SerPlayer;

/// <summary>Writes small synthetic .ser files (mono 8/12/16-bit, Bayer RGGB, RGB, with timestamps) and checks decoding.</summary>
static class DecodeTests
{
    const int Wd = 64, Ht = 48, Frames = 20;

    /// <summary>A bright disc on a gradient (mono) or coloured background, moving left to right.</summary>
    static string Write(string name, int colorId, int depth, int fps)
    {
        int planes = colorId >= 100 ? 3 : 1, bps = depth > 8 ? 2 : 1, max = (1 << depth) - 1;
        string path = Path.Combine(global::SerPlayer.H.Out, name);
        using var f = new BinaryWriter(File.Create(path));
        f.Write(Encoding.ASCII.GetBytes("LUCAM-RECORDER"));
        foreach (int v in new[] { 0, colorId, 0, Wd, Ht, depth, Frames }) f.Write(v);
        foreach (var s in new[] { "Observer", "Camera", "Scope" }) f.Write(Encoding.ASCII.GetBytes(s.PadRight(40, '\0')));
        f.Write(0L); f.Write(0L);
        for (int n = 0; n < Frames; n++)
        {
            double cx = Wd * (0.2 + 0.6 * n / (Frames - 1)), cy = Ht / 2.0;
            for (int y = 0; y < Ht; y++)
                for (int x = 0; x < Wd; x++)
                {
                    bool inside = (x - cx) * (x - cx) + (y - cy) * (y - cy) < (Ht / 4.0) * (Ht / 4.0);
                    int[] vals;
                    if (planes == 3) vals = new[] { inside ? max : 0, inside ? max / 2 : 0, max / 4 };
                    else if (colorId == 8)
                    {
                        var (r, g, b) = inside ? (max, max / 2, 0) : (0, 0, max / 4);
                        vals = new[] { x % 2 == 0 && y % 2 == 0 ? r : x % 2 == 1 && y % 2 == 1 ? b : g };
                    }
                    else vals = new[] { inside ? max : max * x / (Wd - 1) };
                    foreach (int v in vals) { if (bps == 2) f.Write((ushort)v); else f.Write((byte)v); }
                }
        }
        const long t0 = 638000000000000000;
        for (int n = 0; n < Frames; n++) f.Write(t0 + (long)(n * 1e7 / fps));
        return path;
    }

    static Color Pixel(SerFile s, int frame, int x, int y)
    {
        using var bmp = new Bitmap(s.Width, s.Height, PixelFormat.Format32bppPArgb);
        s.ReadFrame(frame, bmp);
        return bmp.GetPixel(x, y);
    }

    static bool Near(Color c, int r, int g, int b, int tol = 3) =>
        Math.Abs(c.R - r) <= tol && Math.Abs(c.G - g) <= tol && Math.Abs(c.B - b) <= tol;

    public static void Run()
    {
        // frame 10: the disc is centred near x = 32
        foreach (var (name, colorId, depth, fps, inR, inG, inB, label) in new[]
        {
            ("mono8.ser", 0, 8, 50, 255, 255, 255, "Mono"),
            ("mono12.ser", 0, 12, 120, 255, 255, 255, "Mono"),
            ("rggb16.ser", 8, 16, 50, 255, 127, 0, "Bayer RGGB"),
            ("rgb8.ser", 100, 8, 50, 255, 127, 63, "RGB"),
        })
        {
            using var s = new SerFile(Write(name, colorId, depth, fps));
            H.Check(s.Width == Wd && s.Height == Ht && s.FrameCount == Frames && s.BitDepth == depth && s.ColorName == label,
                $"{name}: {s.Width}x{s.Height}, {s.FrameCount} frames, {s.BitDepth}-bit {s.ColorName}");
            H.Check(s.DetectedFps is double d && Math.Abs(d - fps) < 0.5, $"{name}: fps from timestamps = {s.DetectedFps:0.0} (expected {fps})");
            var c = Pixel(s, 10, Wd / 2, Ht / 2);
            H.Check(Near(c, inR, inG, inB), $"{name}: disc pixel R{c.R} G{c.G} B{c.B} (expected {inR},{inG},{inB})");
        }

        // debayer preview override: forcing BGGR on an RGGB file swaps red and blue; Off shows raw mono samples
        using (var s = new SerFile(Path.Combine(H.Out, "rggb16.ser")))
        {
            s.DebayerOverride = 11;
            var c = Pixel(s, 10, Wd / 2, Ht / 2);
            H.Check(c.B > 200 && c.R < 60, $"force BGGR swaps red/blue: R{c.R} G{c.G} B{c.B}");
            s.DebayerOverride = 0;
            var raw = Pixel(s, 10, Wd / 2, Ht / 2);
            H.Check(raw.R == raw.G && raw.G == raw.B, $"debayer off is grey: R{raw.R} G{raw.G} B{raw.B}");
        }
    }
}
