using System.Drawing.Drawing2D;

namespace SerPlayer;

/// <summary>
/// "Add video" glyph for the start screen: a film frame with perforated side strips and a play triangle, with a
/// circled plus cut into its bottom-right corner. Built from simple shapes in a 1000-unit square (GDI+ flattens curves
/// to about a quarter of a unit, so the shapes need to be large), then scaled into place.
/// </summary>
static class AddVideoIcon
{
    const float K = 1000f, Stroke = 0.062f * K;

    /// <summary>The glyph's ink bounds in build units, so it can be centred by what's actually drawn.</summary>
    static readonly RectangleF Ink = MeasureInk();

    static RectangleF MeasureInk()
    {
        using var region = Build();
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        return region.GetBounds(g);
    }

    /// <summary>Fills the glyph into <paramref name="r"/>, scaled to fit and centred on its visible shape.</summary>
    public static void Fill(Graphics g, RectangleF r, Brush brush)
    {
        using var region = Build();
        float s = Math.Min(r.Width / Ink.Width, r.Height / Ink.Height);
        float ox = r.X + (r.Width - Ink.Width * s) / 2 - Ink.X * s, oy = r.Y + (r.Height - Ink.Height * s) / 2 - Ink.Y * s;
        using var m = new Matrix(s, 0, 0, s, ox, oy);
        region.Transform(m);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillRegion(brush, region);
        g.SmoothingMode = smooth;
    }

    static Region Build()
    {
        // Film frame: rounded outline with a perforated strip down each side.
        var frame = new RectangleF(0.05f * K, 0.17f * K, 0.74f * K, 0.62f * K);
        using var outline = Outline(RoundRect(frame, 0.11f * K), Stroke);
        var region = new Region(outline);
        foreach (float x in new[] { frame.Left + 0.16f * K, frame.Right - 0.16f * K })
            region.Union(new RectangleF(x - Stroke / 2, frame.Top, Stroke, frame.Height));
        foreach (float cx in new[] { frame.Left + 0.08f * K, frame.Right - 0.08f * K })
            for (int i = 0; i < 3; i++)
            {
                float cy = frame.Top + frame.Height * (0.26f + 0.24f * i);
                using var hole = RoundRect(new RectangleF(cx - 0.03f * K, cy - 0.035f * K, 0.06f * K, 0.07f * K), 0.015f * K);
                region.Union(hole);
            }

        // Play triangle in the middle of the frame, with softened corners.
        float pcx = frame.Left + frame.Width / 2 + 0.015f * K, pcy = frame.Top + frame.Height / 2, ps = 0.11f * K;
        using (var play = new GraphicsPath())
        {
            play.AddPolygon(new[] { new PointF(pcx - ps * 0.8f, pcy - ps), new PointF(pcx + ps, pcy), new PointF(pcx - ps * 0.8f, pcy + ps) });
            using var pen = new Pen(Color.Black, 0.035f * K) { LineJoin = LineJoin.Round };
            play.Widen(pen);
            region.Union(play);
            using var core = new GraphicsPath();
            core.AddPolygon(new[] { new PointF(pcx - ps * 0.8f, pcy - ps), new PointF(pcx + ps, pcy), new PointF(pcx - ps * 0.8f, pcy + ps) });
            region.Union(core);
        }

        // Circled plus over the bottom-right corner, with a gap cut out of the frame around it.
        var c = new PointF(0.77f * K, 0.75f * K);
        const float ringR = 0.175f * K, gap = 0.055f * K;
        using (var cut = new GraphicsPath())
        {
            cut.AddEllipse(c.X - ringR - gap, c.Y - ringR - gap, 2 * (ringR + gap), 2 * (ringR + gap));
            region.Exclude(cut);
        }
        using (var ring = new GraphicsPath())
        {
            ring.AddEllipse(c.X - ringR, c.Y - ringR, 2 * ringR, 2 * ringR);
            using var widened = Outline(ring, Stroke);
            region.Union(widened);
        }
        float arm = 0.085f * K;
        using (var plus = new GraphicsPath())
        {
            plus.AddLine(c.X - arm, c.Y, c.X + arm, c.Y);
            plus.StartFigure();
            plus.AddLine(c.X, c.Y - arm, c.X, c.Y + arm);
            using var pen = new Pen(Color.Black, Stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            plus.Widen(pen);
            region.Union(plus);
        }
        return region;
    }

    /// <summary>The outline of <paramref name="shape"/> as a filled path of the given stroke width.</summary>
    static GraphicsPath Outline(GraphicsPath shape, float width)
    {
        using var pen = new Pen(Color.Black, width) { LineJoin = LineJoin.Round };
        var p = (GraphicsPath)shape.Clone();
        p.Widen(pen);
        shape.Dispose();
        return p;
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
