using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SerPlayer;

/// <summary>Borderless, fully custom-drawn player window with a frosted-glass control overlay.</summary>
sealed class PlayerForm : Form
{
    enum Hit { None, Center, Seek, Play, Rew, Fwd, Bright, Gamma, Loop, Full, More, Min, Max, Close }

    static readonly Hit[] HitOrder = { Hit.Min, Hit.Max, Hit.Close, Hit.Seek, Hit.Play, Hit.Rew, Hit.Fwd, Hit.Bright, Hit.Gamma, Hit.Loop, Hit.Full, Hit.More, Hit.Center };
    const int SkipSeconds = 10;
    const double HideAfterSeconds = 2.5;

    SerFile? _ser;
    Bitmap? _bmp, _blur, _view;
    bool _blurDirty;
    int _frameVersion;
    (int, float, PointF, Size, float) _viewKey;

    // Brightness slider (volume-style). 0..1, 0.5 = neutral; mapped to a display gamma from 4 (dark) to 0.25 (bright).
    float _bright = 0.5f;
    bool _brightDragging;
    readonly byte[] _toneLut = new byte[256];
    float _toneLutFor = float.NaN;
    float Gamma => MathF.Pow(2f, (0.5f - _bright) * 4f);
    bool ToneActive => _bright != 0.5f;

    byte[] ToneLut()
    {
        if (_toneLutFor != _bright)
        {
            double g = Gamma;
            for (int v = 0; v < 256; v++) _toneLut[v] = (byte)Math.Round(255 * Math.Pow(v / 255.0, g));
            _toneLutFor = _bright;
        }
        return _toneLut;
    }

    void SetBrightness(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (Math.Abs(t - 0.5f) < 0.045f) t = 0.5f; // detent at neutral (~5 px either side; keyboard/wheel 0.05 steps pass it)
        if (t == _bright) return;
        _bright = t;
        _blurDirty = true;
        Invalidate();
    }
    readonly Font _titleFont = new("Segoe UI Semibold", 12.5f);
    readonly Font _smallFont = new("Segoe UI", 8.5f);
    readonly Font _hintFont = new("Segoe UI", 11f);
    int _frame = -1, _playStartFrame;
    bool _playing, _loop = true;
    int? _debayer; // debayer preview: null = Auto (header), 0 = Off, 8..11 = forced pattern; kept across files
    double _fps = 30;
    readonly Stopwatch _clock = new();
    readonly Stopwatch _uptime = Stopwatch.StartNew();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 10 };
    readonly ContextMenuStrip _menu = new();

    Hit _hover, _pressed;
    bool _seeking, _cursorHidden, _fullscreen, _ignoreUp;
    double _lastActivity = double.NegativeInfinity;
    Point _lastMousePos = new(int.MinValue, int.MinValue);
    float _ui = 1f; // overlay opacity 0..1

    // Zoom / pan: _zoom is relative to fit-to-window, _center is the image point (0..1) at the view centre.
    const float MaxZoom = 64f;
    float _zoom = 1f;
    PointF _center = new(0.5f, 0.5f);
    bool _dragArmed, _panning;
    Point _dragStart;
    PointF _dragCenter;
    ulong _gestureDistance;
    Point _gesturePoint;
    Rectangle _restoreBounds;
    FormWindowState _restoreState;

    public PlayerForm(string? path)
    {
        Text = "SER Player";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        ClientSize = new Size(P(1024), P(640));
        MinimumSize = new Size(P(560), P(340));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;

        BuildMenu();
        CrashLog.Context = () => _ser == null ? "no file open" :
            $"file '{_ser.Path}' {_ser.Width}x{_ser.Height} {_ser.BitDepth}-bit {_ser.ColorName} frames={_ser.FrameCount}; " +
            $"frame={_frame} playing={_playing} fps={_fps:0.#} zoom={_zoom:0.##} center=({_center.X:0.###},{_center.Y:0.###}) " +
            $"client={ClientSize.Width}x{ClientSize.Height} dpi={DeviceDpi} fullscreen={_fullscreen} busy={_busy} skippedPaints={_skippedPaints}";
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) Open(f[0]); };

        if (path != null) Shown += (_, _) => Open(path);
    }

    // ───────────────────────── window chrome ─────────────────────────

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000 | 0x00010000; // WS_MINIMIZEBOX | WS_MAXIMIZEBOX: taskbar minimize + snap
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetRoundedCorners(true);
        // Enable all touch gestures (pinch-zoom + pan in every direction) for touchscreens.
        try
        {
            var cfg = new GESTURECONFIG { dwID = 0, dwWant = 1, dwBlock = 0 };
            SetGestureConfig(Handle, 0, 1, new[] { cfg }, Marshal.SizeOf<GESTURECONFIG>());
        }
        catch { }
    }

    void SetRoundedCorners(bool round)
    {
        int pref = round ? 2 : 1; // DWMWCP_ROUND / DWMWCP_DONOTROUND (Windows 11; ignored elsewhere)
        try { DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int)); } catch { }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_GETMINMAXINFO = 0x24, HTCLIENT = 1;
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if (m.Result == HTCLIENT)
            {
                long lp = m.LParam.ToInt64();
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                m.Result = NcHitTest(p);
            }
            return;
        }
        if (m.Msg == WM_GETMINMAXINFO)
        {
            base.WndProc(ref m);
            // Keep maximized borderless window off the taskbar.
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            var mon = MonitorFromWindow(Handle, 2);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
            {
                mmi.ptMaxPosition = new POINT { X = mi.rcWork.Left - mi.rcMonitor.Left, Y = mi.rcWork.Top - mi.rcMonitor.Top };
                mmi.ptMaxSize = new POINT { X = mi.rcWork.Right - mi.rcWork.Left, Y = mi.rcWork.Bottom - mi.rcWork.Top };
                Marshal.StructureToPtr(mmi, m.LParam, false);
            }
            return;
        }
        if (m.Msg == 0x00A0 /* WM_NCMOUSEMOVE: the top drag strip */ && !_dragArmed)
        {
            if (MousePosition != _lastMousePos) { _lastMousePos = MousePosition; Activity(); }
        }
        if (m.Msg == WM_GESTURE && HandleGesture(m.LParam))
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    const int WM_GESTURE = 0x0119;

    /// <summary>Touchscreen pinch-zoom and pan. (Precision-touchpad pinch arrives as Ctrl+wheel instead.)</summary>
    bool HandleGesture(IntPtr hGesture)
    {
        const int GID_BEGIN = 1, GID_END = 2, GID_ZOOM = 3, GID_PAN = 4, GF_BEGIN = 1;
        var gi = new GESTUREINFO { cbSize = Marshal.SizeOf<GESTUREINFO>() };
        if (!GetGestureInfo(hGesture, ref gi)) return false;
        var pt = PointToClient(new Point(gi.ptsLocation.x, gi.ptsLocation.y));
        bool begin = (gi.dwFlags & GF_BEGIN) != 0;
        bool handled = true;
        switch (gi.dwID)
        {
            case GID_ZOOM:
                if (!begin && _gestureDistance > 0 && gi.ullArguments > 0)
                    ZoomAt((float)gi.ullArguments / _gestureDistance, pt);
                _gestureDistance = gi.ullArguments;
                break;
            case GID_PAN:
                if (!begin && _zoom > 1f) PanBy(pt.X - _gesturePoint.X, pt.Y - _gesturePoint.Y);
                _gesturePoint = pt;
                break;
            case GID_BEGIN:
            case GID_END:
                handled = false; // must go to DefWindowProc
                break;
            default:
                handled = false;
                break;
        }
        if (handled) CloseGestureInfoHandle(hGesture);
        return handled;
    }

    IntPtr NcHitTest(Point p)
    {
        const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
            HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        if (_fullscreen) return HTCLIENT;
        if (WindowState == FormWindowState.Normal)
        {
            int b = P(6);
            bool l = p.X < b, r = p.X >= ClientSize.Width - b, t = p.Y < b, d = p.Y >= ClientSize.Height - b;
            if (t && l) return HTTOPLEFT;
            if (t && r) return HTTOPRIGHT;
            if (d && l) return HTBOTTOMLEFT;
            if (d && r) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (d) return HTBOTTOM;
        }
        if (HitTest(p) != Hit.None) return HTCLIENT;
        if (p.Y < P(48)) return HTCAPTION; // drag strip along the top
        return HTCLIENT;
    }

    void ToggleMaximize()
    {
        if (_fullscreen) { ToggleFullscreen(); return; }
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoreState = WindowState;
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            _restoreBounds = Bounds;
            _fullscreen = true;
            SetRoundedCorners(false);
            Bounds = Screen.FromHandle(Handle).Bounds;
        }
        else
        {
            _fullscreen = false;
            Bounds = _restoreBounds;
            WindowState = _restoreState;
            SetRoundedCorners(true);
        }
        Invalidate();
    }

    // ───────────────────────── layout ─────────────────────────

    int P(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    Rectangle PanelRect
    {
        get
        {
            int m = P(22), h = P(118);
            int w = Math.Min(ClientSize.Width - 2 * m, P(1180));
            return new Rectangle((ClientSize.Width - w) / 2, ClientSize.Height - m - h, w, h);
        }
    }

    int Pad => P(24);
    int SeekY => PanelRect.Top + P(66);
    Rectangle SeekRect { get { var pr = PanelRect; return new Rectangle(pr.Left + Pad, SeekY - P(9), pr.Width - 2 * Pad, P(18)); } }

    Rectangle WindowButtonsRect
    {
        get { int w = P(46) * 3, h = P(34), m = P(12); return new Rectangle(ClientSize.Width - m - w, m, w, h); }
    }

    int GammaLabelW => P(52); // room for the value shown right of the brightness slider

    Rectangle Rect(Hit h)
    {
        var pr = PanelRect;
        int bs = P(36), rowY = pr.Top + P(96) - bs / 2, step = P(42);
        int left = pr.Left + Pad - P(8), right = pr.Right - Pad + P(8);
        var wb = WindowButtonsRect;
        int d = P(78);
        return h switch
        {
            Hit.Seek => SeekRect,
            Hit.Play => new Rectangle(left, rowY, bs, bs),
            Hit.Rew => new Rectangle(left + step, rowY, bs, bs),
            Hit.Fwd => new Rectangle(left + 2 * step, rowY, bs, bs),
            Hit.More => new Rectangle(right - bs, rowY, bs, bs),
            Hit.Full => new Rectangle(right - bs - step, rowY, bs, bs),
            Hit.Loop => new Rectangle(right - bs - 2 * step, rowY, bs, bs),
            Hit.Gamma => new Rectangle(right - bs - 2 * step - P(14) - GammaLabelW - P(110), rowY, P(110), bs),
            Hit.Bright => new Rectangle(right - bs - 2 * step - P(14) - GammaLabelW - P(110) - bs - P(2), rowY, bs, bs),
            Hit.Min => new Rectangle(wb.Left, wb.Top, wb.Width / 3, wb.Height),
            Hit.Max => new Rectangle(wb.Left + wb.Width / 3, wb.Top, wb.Width / 3, wb.Height),
            Hit.Close => new Rectangle(wb.Left + 2 * wb.Width / 3, wb.Top, wb.Width - 2 * wb.Width / 3, wb.Height),
            // Playing: the window's centre. Empty: the add-video button and its hint, centred together above the panel.
            Hit.Center => _ser != null
                ? new Rectangle((ClientSize.Width - d) / 2, (ClientSize.Height - d) / 2, d, d)
                : new Rectangle((ClientSize.Width - d) / 2, (PanelRect.Top - (d + P(8) + TextRenderer.MeasureText("Ag", _hintFont).Height)) / 2, d, d),
            _ => Rectangle.Empty,
        };
    }

    static bool NeedsFile(Hit h) => h is Hit.Seek or Hit.Play or Hit.Rew or Hit.Fwd or Hit.Loop or Hit.Bright or Hit.Gamma;

    Hit HitTest(Point p)
    {
        if (_ui < 0.5f) return Hit.None;
        foreach (var h in HitOrder)
        {
            if (h == Hit.Center)
            {
                var c = Rect(h);
                float dx = p.X - (c.Left + c.Width / 2f), dy = p.Y - (c.Top + c.Height / 2f);
                if (dx * dx + dy * dy <= c.Width * c.Width / 4f) return h;
            }
            else if (Rect(h).Contains(p) && (_ser != null || !NeedsFile(h))) return h; // inactive without a file: no hover, no click
        }
        return Hit.None;
    }

    RectangleF VideoRectF
    {
        get
        {
            if (_bmp == null) return RectangleF.Empty;
            float s = Math.Min((float)ClientSize.Width / _bmp.Width, (float)ClientSize.Height / _bmp.Height) * _zoom;
            float w = _bmp.Width * s, h = _bmp.Height * s;
            return new RectangleF(ClientSize.Width / 2f - _center.X * w, ClientSize.Height / 2f - _center.Y * h, w, h);
        }
    }

    Rectangle VideoRect => Rectangle.Round(VideoRectF);

    // ───────────────────────── zoom / pan ─────────────────────────

    /// <summary>Keeps the image covering the window: centred on an axis where it fits, edge-clamped where it overflows.</summary>
    void ClampCenter()
    {
        var vr = VideoRectF;
        if (vr.Width <= 0) return;
        float hx = ClientSize.Width / 2f / vr.Width, hy = ClientSize.Height / 2f / vr.Height;
        _center.X = hx >= 0.5f ? 0.5f : Math.Clamp(_center.X, hx, 1 - hx);
        _center.Y = hy >= 0.5f ? 0.5f : Math.Clamp(_center.Y, hy, 1 - hy);
    }

    /// <summary>Zooms by <paramref name="factor"/>, keeping the image point under <paramref name="anchor"/> fixed.</summary>
    void ZoomAt(float factor, Point anchor)
    {
        if (_bmp == null) return;
        var vr = VideoRectF;
        float u = (anchor.X - vr.X) / vr.Width, v = (anchor.Y - vr.Y) / vr.Height;
        float z = Math.Clamp(_zoom * factor, 1f, MaxZoom);
        if (Math.Abs(z - 1f) < 0.02f) z = 1f;
        if (z == _zoom) return;
        _zoom = z;
        var nr = VideoRectF;
        _center = new PointF(u - (anchor.X - ClientSize.Width / 2f) / nr.Width, v - (anchor.Y - ClientSize.Height / 2f) / nr.Height);
        ClampCenter();
        Invalidate();
    }

    void ZoomCentered(float factor) => ZoomAt(factor, new Point(ClientSize.Width / 2, ClientSize.Height / 2));

    void ResetZoom()
    {
        _zoom = 1f;
        _center = new PointF(0.5f, 0.5f);
        Invalidate();
    }

    void PanBy(float dx, float dy)
    {
        var vr = VideoRectF;
        if (vr.Width <= 0) return;
        _center = new PointF(_center.X - dx / vr.Width, _center.Y - dy / vr.Height);
        ClampCenter();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampCenter();
    }

    // ───────────────────────── painting ─────────────────────────

    Color C(int a, int r, int g, int b) => Color.FromArgb(Math.Clamp((int)(a * _ui), 0, 255), r, g, b);
    Color W(int a) => C(a, 255, 255, 255);

    // Safety net against re-entrancy while a frame is being decoded/rendered (bitmaps are locked then). NoPump.For keeps
    // the UI thread from pumping messages during that work, so this should never trigger; _skippedPaints counts it if it does.
    int _busy, _skippedPaints;
    bool _repaintPending, _paintFailed;

    void EndBusy()
    {
        if (--_busy == 0 && _repaintPending)
        {
            _repaintPending = false;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_busy > 0) { _skippedPaints++; _repaintPending = true; return; }
        _busy++;
        try { PaintCore(e); }
        catch (Exception ex)
        {
            // Never let one bad paint leave WinForms' permanent red-X "paint failed" state; rebuild caches next time.
            CrashLog.Write(ex, "Paint error (recovered)");
            _viewKey = default;
            _blurDirty = true;
            if (!_paintFailed) { _paintFailed = true; BeginInvoke(() => { _paintFailed = false; Invalidate(); }); }
        }
        finally { EndBusy(); }
    }

    void PaintCore(PaintEventArgs e)
    {
        var g = e.Graphics;

        if (_bmp != null && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            // The scaled frame is cached; hover/fade repaints just copy it 1:1.
            RenderView();
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            var clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, _view!.Size));
            g.DrawImage(_view, clip, clip.X, clip.Y, clip.Width, clip.Height, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
        }
        else
        {
            using var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(18, 22, 34), Color.FromArgb(6, 8, 12), 90f);
            g.FillRectangle(bg, ClientRectangle);
            var area = new Rectangle(0, 0, ClientSize.Width, PanelRect.Top);
            TextRenderer.DrawText(g, "Drop a .ser file here, or click the button to browse", _hintFont,
                new Rectangle(area.X, Rect(Hit.Center).Bottom + P(8), area.Width, P(40)), Color.FromArgb(140, 150, 165),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }

        if (_ui <= 0.01f) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (_blurDirty) UpdateBlur();

        // Window buttons
        var wb = WindowButtonsRect;
        using (var path = RoundRect(wb, wb.Height / 2f)) Glass(g, path, wb);
        foreach (var h in new[] { Hit.Min, Hit.Max, Hit.Close })
        {
            var r = Rect(h);
            if (_hover == h)
            {
                using var hp = RoundRect(r, wb.Height / 2f);
                var st = g.Save();
                using (var clip = RoundRect(wb, wb.Height / 2f)) g.SetClip(clip);
                using var hb = new SolidBrush(h == Hit.Close ? C(220, 220, 50, 50) : W(40));
                g.FillRectangle(hb, r);
                g.Restore(st);
            }
            DrawWindowGlyph(g, h, r);
        }

        // Centre button: "add video" (browse) until a file is open, then play/pause
        var cr = Rect(Hit.Center);
        using (var cp = new GraphicsPath()) { cp.AddEllipse(cr); Glass(g, cp, cr, _hover == Hit.Center ? 40 : 60); }
        var gr = RectangleF.Inflate(cr, -cr.Width * 0.3f, -cr.Height * 0.3f);
        if (_ser == null) AddVideoGlyph(g, RectangleF.Inflate(cr, -cr.Width * 0.2f, -cr.Height * 0.2f), W(245));
        else if (_playing) PauseGlyph(g, gr, W(245));
        else PlayGlyph(g, new RectangleF(gr.X + gr.Width * 0.08f, gr.Y, gr.Width, gr.Height), W(245));

        // Control panel
        var pr = PanelRect;
        using (var pp = RoundRect(pr, P(14))) Glass(g, pp, pr);
        DrawPanelContents(g, pr);
    }

    void DrawPanelContents(Graphics g, Rectangle pr)
    {
        var titleFont = _titleFont;
        var smallFont = _smallFont;
        using var titleBrush = new SolidBrush(W(245));
        using var subBrush = new SolidBrush(W(160));
        using var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

        string title = _ser != null ? Path.GetFileName(_ser.Path) : "SER Player";
        string right = "";
        string sub = "No file loaded";
        if (_ser != null)
        {
            right = $"Frame {_frame + 1:N0} / {_ser.FrameCount:N0}    {Time(_frame / _fps)} / {Time((_ser.FrameCount - 1) / _fps)}";
            sub = $"{_ser.Width}×{_ser.Height}  ·  {_ser.BitDepth}-bit {ColorDescription()}{(_ser.BitDepth > 8 && _ser.BigEndian ? " (BE)" : "")}  ·  {_fps:0.#} fps";
            if (_zoom > 1f) sub += $"  ·  Zoom {_zoom * 100:0}%";
            if (_ser.FrameTimeUtc(_frame) is DateTime dt) sub += $"  ·  {dt:yyyy-MM-dd HH:mm:ss.fff} UTC";
        }

        float rightW = g.MeasureString(right, smallFont).Width;
        g.DrawString(title, titleFont, titleBrush, new RectangleF(pr.Left + Pad, pr.Top + P(14), pr.Width - 2 * Pad, P(26)), sf);
        g.DrawString(sub, smallFont, subBrush, new RectangleF(pr.Left + Pad, pr.Top + P(40), pr.Width - 2 * Pad - rightW - P(16), P(18)), sf);
        g.DrawString(right, smallFont, subBrush, pr.Right - Pad - rightW, pr.Top + P(40));

        // Seek bar
        var sr = SeekRect;
        float frac = _ser == null || _ser.FrameCount <= 1 ? 0 : _frame / (float)(_ser.FrameCount - 1);
        float x = sr.Left + frac * sr.Width;
        bool active = _hover == Hit.Seek || _seeking;
        float th = active ? P(4) : P(2);
        using (var track = RoundRect(new RectangleF(sr.Left, SeekY - th / 2, sr.Width, th), th / 2))
        using (var tb = new SolidBrush(W(60))) g.FillPath(tb, track);
        if (x > sr.Left)
            using (var done = RoundRect(new RectangleF(sr.Left, SeekY - th / 2, x - sr.Left, th), th / 2))
            using (var db = new SolidBrush(W(225))) g.FillPath(db, done);
        if (_ser != null)
        {
            float dr = active ? P(7) : P(5);
            using var kb = new SolidBrush(W(255));
            g.FillEllipse(kb, x - dr, SeekY - dr, 2 * dr, 2 * dr);
        }

        // Buttons
        bool has = _ser != null;
        DrawBrightnessSlider(g, has);
        foreach (var h in new[] { Hit.Play, Hit.Rew, Hit.Fwd, Hit.Bright, Hit.Loop, Hit.Full, Hit.More })
        {
            var r = Rect(h);
            bool enabled = has || h is Hit.More or Hit.Full;
            if (_hover == h && enabled)
                using (var hb = new SolidBrush(W(34))) g.FillEllipse(hb, r);
            var col = !enabled ? W(70) : h == Hit.Loop && !_loop ? W(110) : W(240);
            var gr = RectangleF.Inflate(r, -r.Width * 0.24f, -r.Height * 0.24f);
            switch (h)
            {
                case Hit.Play: if (_playing) PauseGlyph(g, gr, col); else PlayGlyph(g, gr, col); break;
                case Hit.Rew: SkipGlyph(g, r, col, false); break;
                case Hit.Fwd: SkipGlyph(g, r, col, true); break;
                case Hit.Loop: LoopGlyph(g, gr, col); break;
                case Hit.Bright: SunGlyph(g, gr, col); break;
                case Hit.Full: FullGlyph(g, gr, col, _fullscreen); break;
                case Hit.More: MoreGlyph(g, gr, col); break;
            }
        }
    }

    void DrawBrightnessSlider(Graphics g, bool enabled)
    {
        var r = Rect(Hit.Gamma);
        float cy = r.Top + r.Height / 2f, l = r.Left + P(6), w = r.Width - P(12);
        bool active = enabled && (_hover == Hit.Gamma || _brightDragging);
        float th = active ? P(4) : P(2), x = l + _bright * w;
        using (var track = RoundRect(new RectangleF(l, cy - th / 2, w, th), th / 2))
        using (var tb = new SolidBrush(W(enabled ? 60 : 30))) g.FillPath(tb, track);
        if (x > l)
            using (var done = RoundRect(new RectangleF(l, cy - th / 2, x - l, th), th / 2))
            using (var db = new SolidBrush(W(enabled ? 225 : 70))) g.FillPath(db, done);
        // neutral (gamma 1) tick
        using (var tick = new Pen(W(enabled ? 120 : 50), Math.Max(1f, P(1.5f))))
            g.DrawLine(tick, l + w / 2, cy - P(5), l + w / 2, cy + P(5));
        if (enabled)
        {
            float dr = active ? P(7) : P(5);
            using var kb = new SolidBrush(W(255));
            g.FillEllipse(kb, x - dr, cy - dr, 2 * dr, 2 * dr);
        }

        // Value to the right of the slider (display gamma; higher = brighter, 1.00 = neutral)
        using var vb = new SolidBrush(!enabled ? W(70) : ToneActive ? W(235) : W(150));
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        g.DrawString($"{1 / Gamma:0.00}", _smallFont, vb, new RectangleF(r.Right + P(6), r.Top, GammaLabelW, r.Height), sf);
    }

    void SunGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c, 1.6f);
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, cr = r.Width * 0.2f;
        g.DrawEllipse(pen, cx - cr, cy - cr, 2 * cr, 2 * cr);
        // half-filled disc = brightness/contrast icon
        using (var b = new SolidBrush(c)) g.FillPie(b, cx - cr, cy - cr, 2 * cr, 2 * cr, 90, 180);
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            float r0 = r.Width * 0.34f, r1 = r.Width * 0.48f;
            g.DrawLine(pen, cx + (float)Math.Cos(a) * r0, cy + (float)Math.Sin(a) * r0, cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1);
        }
    }

    static string Time(double s)
    {
        if (s < 0 || double.IsNaN(s)) s = 0;
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{(int)t.TotalMinutes}:{t:ss\\.f}";
    }

    /// <summary>Frosted glass: blurred video behind the shape, a dark tint, a soft top highlight and a hairline border.</summary>
    void Glass(Graphics g, GraphicsPath path, Rectangle bounds, int tint = 70)
    {
        var st = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        using (var black = new SolidBrush(C(200, 0, 0, 0))) g.FillRectangle(black, bounds);
        if (_blur != null)
        {
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            if (_ui < 1f) ia.SetColorMatrix(new ColorMatrix { Matrix33 = _ui }); // colour matrix is the slow path; only while fading
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            var vr = VideoRectF;
            var dest = Rectangle.Round(RectangleF.Intersect(vr, bounds));
            if (!dest.IsEmpty)
            {
                float sx = _blur.Width / vr.Width, sy = _blur.Height / vr.Height;
                g.DrawImage(_blur, dest, (dest.X - vr.X) * sx, (dest.Y - vr.Y) * sy, dest.Width * sx, dest.Height * sy, GraphicsUnit.Pixel, ia);
            }
        }
        using (var t = new SolidBrush(C(tint, 70, 80, 95))) g.FillRectangle(t, bounds);
        using (var hl = new LinearGradientBrush(new Rectangle(bounds.X, bounds.Y - 1, bounds.Width, bounds.Height + 2), W(30), W(6), 90f))
            g.FillRectangle(hl, bounds);
        g.Restore(st);
        using var border = new Pen(W(45), 1f);
        g.DrawPath(border, path);
    }

    /// <summary>Builds the tiny image the glass blur is stretched from, by averaging a sparse grid of samples per cell.</summary>
    unsafe void UpdateBlur()
    {
        _blurDirty = false;
        if (_bmp == null) return;
        int bw = 40, bh = Math.Max(2, (int)Math.Round(bw * _bmp.Height / (double)_bmp.Width));
        if (_blur == null || _blur.Width != bw || _blur.Height != bh)
        {
            _blur?.Dispose();
            _blur = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
        }
        int sw = _bmp.Width, sh = _bmp.Height;
        const int N = 5; // samples per cell axis
        BitmapData? sd = null, bd = null;
        try
        {
            sd = _bmp.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            bd = _blur.LockBits(new Rectangle(0, 0, bw, bh), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            for (int by = 0; by < bh; by++)
            {
                uint* outRow = (uint*)((byte*)bd.Scan0 + by * bd.Stride);
                for (int bx = 0; bx < bw; bx++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int j = 0; j < N; j++)
                    {
                        int y = Math.Min(sh - 1, (int)((by + (j + 0.5) / N) * sh / bh));
                        uint* row = (uint*)((byte*)sd.Scan0 + (long)y * sd.Stride);
                        for (int i = 0; i < N; i++)
                        {
                            uint p = row[Math.Min(sw - 1, (int)((bx + (i + 0.5) / N) * sw / bw))];
                            r += (int)(p >> 16) & 0xFF; g += (int)(p >> 8) & 0xFF; b += (int)p & 0xFF;
                        }
                    }
                    r /= N * N; g /= N * N; b /= N * N;
                    if (ToneActive) { var lut = ToneLut(); r = lut[r]; g = lut[g]; b = lut[b]; }
                    outRow[bx] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
                }
            }
        }
        finally
        {
            if (sd != null) _bmp.UnlockBits(sd);
            if (bd != null) _blur.UnlockBits(bd);
        }
    }

    /// <summary>
    /// Renders the visible part of the frame into a window-sized cache (parallel; nearest-neighbour when magnified,
    /// bilinear near 1:1, box-filtered when shrinking). Skipped when frame, zoom, pan and size are unchanged.
    /// </summary>
    unsafe void RenderView()
    {
        if (_bmp == null) return;
        var size = ClientSize;
        if (_view == null || _view.Size != size)
        {
            _view?.Dispose();
            _view = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            _viewKey = default;
        }
        var key = (_frameVersion, _zoom, _center, size, _bright);
        if (key == _viewKey) return;
        _viewKey = key;

        int W = size.Width, H = size.Height, sw = _bmp.Width, sh = _bmp.Height;
        var vr = VideoRectF;
        float scale = vr.Width / sw;
        int mode = scale >= 2f ? 0 : scale >= 0.6f ? 1 : 2; // 0 nearest, 1 bilinear, 2 box
        int x0 = Math.Clamp((int)Math.Floor(vr.X), 0, W), x1 = Math.Clamp((int)Math.Ceiling(vr.Right), 0, W);
        int y0 = Math.Clamp((int)Math.Floor(vr.Y), 0, H), y1 = Math.Clamp((int)Math.Ceiling(vr.Bottom), 0, H);

        // Per-column / per-row source lookup: index a, index b (next tap or box end), bilinear weight
        static void Axis(int n, float origin, float scale, int srcLen, int mode, int[] a, int[] b, int[] f, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                if (mode == 0)
                {
                    a[i] = Math.Clamp((int)((i + 0.5f - origin) / scale), 0, srcLen - 1);
                }
                else if (mode == 1)
                {
                    float s = (i + 0.5f - origin) / scale - 0.5f;
                    int k = (int)Math.Floor(s);
                    f[i] = (int)((s - k) * 256);
                    a[i] = Math.Clamp(k, 0, srcLen - 1);
                    b[i] = Math.Clamp(k + 1, 0, srcLen - 1);
                }
                else
                {
                    int s0 = Math.Clamp((int)Math.Floor((i - origin) / scale), 0, srcLen - 1);
                    int s1 = Math.Clamp((int)Math.Ceiling((i + 1 - origin) / scale), s0 + 1, srcLen);
                    a[i] = s0; b[i] = s1;
                }
            }
        }
        int[] ca = new int[W], cb = new int[W], cf = new int[W], ra = new int[H], rb = new int[H], rf = new int[H];
        Axis(W, vr.X, scale, sw, mode, ca, cb, cf, x0, x1);
        Axis(H, vr.Y, scale, sh, mode, ra, rb, rf, y0, y1);

        BitmapData? vd = null, sd = null;
        try
        {
            vd = _view.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            sd = _bmp.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            IntPtr dp = vd.Scan0, sp = sd.Scan0;
            int ds = vd.Stride, ss = sd.Stride;
            const uint Black = 0xFF000000u;
            var lutHandle = ToneActive ? GCHandle.Alloc(ToneLut(), GCHandleType.Pinned) : default;
            IntPtr tone = lutHandle.IsAllocated ? lutHandle.AddrOfPinnedObject() : IntPtr.Zero;
            try
            {
            NoPump.For(H, y =>
            {
                uint* d = (uint*)((byte*)dp + (long)y * ds);
                if (y < y0 || y >= y1) { new Span<uint>(d, W).Fill(Black); return; }
                if (x0 > 0) new Span<uint>(d, x0).Fill(Black);
                if (x1 < W) new Span<uint>(d + x1, W - x1).Fill(Black);
                if (mode == 0)
                {
                    uint* s = (uint*)((byte*)sp + (long)ra[y] * ss);
                    for (int x = x0; x < x1; x++) d[x] = s[ca[x]];
                }
                else if (mode == 1)
                {
                    uint* s0 = (uint*)((byte*)sp + (long)ra[y] * ss), s1 = (uint*)((byte*)sp + (long)rb[y] * ss);
                    int fy = rf[y];
                    for (int x = x0; x < x1; x++)
                    {
                        int fx = cf[x];
                        uint top = Lerp(s0[ca[x]], s0[cb[x]], fx), bot = Lerp(s1[ca[x]], s1[cb[x]], fx);
                        d[x] = Black | Lerp(top, bot, fy);
                    }
                }
                else
                {
                    int ya = ra[y], yb = rb[y];
                    for (int x = x0; x < x1; x++)
                    {
                        int xa = ca[x], xb = cb[x], r = 0, g = 0, b = 0;
                        for (int sy = ya; sy < yb; sy++)
                        {
                            uint* s = (uint*)((byte*)sp + (long)sy * ss);
                            for (int sx = xa; sx < xb; sx++)
                            {
                                uint p = s[sx];
                                r += (int)(p >> 16) & 0xFF; g += (int)(p >> 8) & 0xFF; b += (int)p & 0xFF;
                            }
                        }
                        int n = (yb - ya) * (xb - xa);
                        d[x] = Black | (uint)(r / n << 16) | (uint)(g / n << 8) | (uint)(b / n);
                    }
                }
                // Display brightness/gamma
                if (tone != IntPtr.Zero)
                {
                    byte* t = (byte*)tone;
                    for (int x = x0; x < x1; x++)
                    {
                        uint p = d[x];
                        d[x] = Black | (uint)t[(p >> 16) & 0xFF] << 16 | (uint)t[(p >> 8) & 0xFF] << 8 | t[p & 0xFF];
                    }
                }
            });
            }
            finally
            {
                if (lutHandle.IsAllocated) lutHandle.Free();
            }
        }
        finally
        {
            if (vd != null) _view.UnlockBits(vd);
            if (sd != null) _bmp.UnlockBits(sd);
        }
    }

    /// <summary>Blends two packed RGB pixels, f = 0..256 (weight of b).</summary>
    static uint Lerp(uint a, uint b, int f)
    {
        uint wa = (uint)(256 - f), wb = (uint)f;
        uint rb = (((a & 0xFF00FFu) * wa + (b & 0xFF00FFu) * wb) >> 8) & 0xFF00FFu;
        uint g = (((a & 0x00FF00u) * wa + (b & 0x00FF00u) * wb) >> 8) & 0x00FF00u;
        return rb | g;
    }

    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    Pen GlyphPen(Color c, float w = 1.8f) => new(c, w * DeviceDpi / 96f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    static void PlayGlyph(Graphics g, RectangleF r, Color c)
    {
        using var b = new SolidBrush(c);
        float h = r.Height * 0.9f, w = h * 0.86f, x = r.X + (r.Width - w) / 2, y = r.Y + (r.Height - h) / 2;
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { new PointF(x, y), new PointF(x + w, y + h / 2), new PointF(x, y + h) });
        using var pen = new Pen(c, h * 0.12f) { LineJoin = LineJoin.Round };
        g.FillPath(b, path);
        g.DrawPath(pen, path);
    }

    static void AddVideoGlyph(Graphics g, RectangleF r, Color c)
    {
        using var b = new SolidBrush(c);
        AddVideoIcon.Fill(g, r, b);
    }

    static void PauseGlyph(Graphics g, RectangleF r, Color c)
    {
        using var b = new SolidBrush(c);
        float h = r.Height * 0.86f, bw = r.Width * 0.24f, gap = r.Width * 0.18f;
        float x = r.X + (r.Width - 2 * bw - gap) / 2, y = r.Y + (r.Height - h) / 2;
        using var p1 = RoundRect(new RectangleF(x, y, bw, h), bw * 0.3f);
        using var p2 = RoundRect(new RectangleF(x + bw + gap, y, bw, h), bw * 0.3f);
        g.FillPath(b, p1);
        g.FillPath(b, p2);
    }

    void SkipGlyph(Graphics g, Rectangle box, Color c, bool forward)
    {
        float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f + P(1), rad = box.Width * 0.3f;
        using var pen = GlyphPen(c, 1.7f);
        // Open circle with an arrowhead at 12 o'clock
        g.DrawArc(pen, cx - rad, cy - rad, 2 * rad, 2 * rad, forward ? -90 : -90, forward ? -290 : 290);
        float a = rad * 0.45f, top = cy - rad;
        float dir = forward ? 1 : -1;
        using var b = new SolidBrush(c);
        g.FillPolygon(b, new[]
        {
            new PointF(cx + dir * a * 1.1f, top),
            new PointF(cx - dir * a * 0.2f, top - a * 0.8f),
            new PointF(cx - dir * a * 0.2f, top + a * 0.8f),
        });
        using var f = new Font("Segoe UI", rad * 0.95f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(SkipSeconds.ToString(), f, b, new RectangleF(cx - rad, cy - rad + P(0.5f), 2 * rad, 2 * rad), sf);
    }

    void LoopGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c);
        var box = new RectangleF(r.X + r.Width * 0.05f, r.Y + r.Height * 0.22f, r.Width * 0.9f, r.Height * 0.56f);
        using (var path = RoundRect(box, box.Height * 0.35f)) g.DrawPath(pen, path);
        using var b = new SolidBrush(c);
        float a = r.Width * 0.16f;
        float tx = box.X + box.Width * 0.62f, bx = box.X + box.Width * 0.38f;
        g.FillPolygon(b, new[] { new PointF(tx + a, box.Top), new PointF(tx - a * 0.3f, box.Top - a * 0.85f), new PointF(tx - a * 0.3f, box.Top + a * 0.85f) });
        g.FillPolygon(b, new[] { new PointF(bx - a, box.Bottom), new PointF(bx + a * 0.3f, box.Bottom - a * 0.85f), new PointF(bx + a * 0.3f, box.Bottom + a * 0.85f) });
    }

    void FullGlyph(Graphics g, RectangleF r, Color c, bool exit)
    {
        using var pen = GlyphPen(c);
        float l = r.Width * 0.3f, i = r.Width * 0.1f;
        var q = RectangleF.Inflate(r, -i, -i);
        if (!exit)
        {
            g.DrawLines(pen, new[] { new PointF(q.Left, q.Top + l), new PointF(q.Left, q.Top), new PointF(q.Left + l, q.Top) });
            g.DrawLines(pen, new[] { new PointF(q.Right - l, q.Top), new PointF(q.Right, q.Top), new PointF(q.Right, q.Top + l) });
            g.DrawLines(pen, new[] { new PointF(q.Right, q.Bottom - l), new PointF(q.Right, q.Bottom), new PointF(q.Right - l, q.Bottom) });
            g.DrawLines(pen, new[] { new PointF(q.Left + l, q.Bottom), new PointF(q.Left, q.Bottom), new PointF(q.Left, q.Bottom - l) });
        }
        else
        {
            g.DrawLines(pen, new[] { new PointF(q.Left, q.Top + l), new PointF(q.Left + l, q.Top + l), new PointF(q.Left + l, q.Top) });
            g.DrawLines(pen, new[] { new PointF(q.Right - l, q.Top), new PointF(q.Right - l, q.Top + l), new PointF(q.Right, q.Top + l) });
            g.DrawLines(pen, new[] { new PointF(q.Right, q.Bottom - l), new PointF(q.Right - l, q.Bottom - l), new PointF(q.Right - l, q.Bottom) });
            g.DrawLines(pen, new[] { new PointF(q.Left + l, q.Bottom), new PointF(q.Left + l, q.Bottom - l), new PointF(q.Left, q.Bottom - l) });
        }
    }

    static void MoreGlyph(Graphics g, RectangleF r, Color c)
    {
        using var b = new SolidBrush(c);
        float d = r.Width * 0.16f, cx = r.X + r.Width / 2;
        for (int i = -1; i <= 1; i++)
            g.FillEllipse(b, cx - d / 2, r.Y + r.Height / 2 + i * r.Height * 0.32f - d / 2, d, d);
    }

    void DrawWindowGlyph(Graphics g, Hit h, Rectangle r)
    {
        using var pen = GlyphPen(W(235), 1.2f);
        float s = P(10), cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        var smooth = g.SmoothingMode;
        switch (h)
        {
            case Hit.Min:
                g.DrawLine(pen, cx - s / 2, cy, cx + s / 2, cy);
                break;
            case Hit.Max:
                if (WindowState == FormWindowState.Maximized || _fullscreen)
                {
                    float o = s * 0.25f;
                    g.DrawRectangle(pen, cx - s / 2, cy - s / 2 + o, s - o, s - o);
                    g.DrawLines(pen, new[] { new PointF(cx - s / 2 + o, cy - s / 2 + o), new PointF(cx - s / 2 + o, cy - s / 2), new PointF(cx + s / 2, cy - s / 2), new PointF(cx + s / 2, cy + s / 2 - o), new PointF(cx + s / 2 - o, cy + s / 2 - o) });
                }
                else
                {
                    using var rp = RoundRect(new RectangleF(cx - s / 2, cy - s / 2, s, s), P(1.5f));
                    g.DrawPath(pen, rp);
                }
                break;
            case Hit.Close:
                g.DrawLine(pen, cx - s / 2, cy - s / 2, cx + s / 2, cy + s / 2);
                g.DrawLine(pen, cx + s / 2, cy - s / 2, cx - s / 2, cy + s / 2);
                break;
        }
        g.SmoothingMode = smooth;
    }

    // ───────────────────────── input ─────────────────────────

    /// <summary>Shows the overlay and restarts its hide countdown.</summary>
    void Activity() => _lastActivity = _uptime.Elapsed.TotalSeconds;

    void HideOverlayNow() => _lastActivity = double.NegativeInfinity;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // Windows sends WM_MOUSEMOVE for things that aren't movement (repaints, cursor changes, window moves); ignore those.
        var screen = PointToScreen(e.Location);
        bool moved = screen != _lastMousePos;
        _lastMousePos = screen;

        if (_seeking) { Activity(); SeekTo(e.X); return; }
        if (_brightDragging) { Activity(); BrightnessTo(e.X); return; }

        if (_dragArmed)
        {
            if (!_panning && _zoom > 1f &&
                (Math.Abs(e.X - _dragStart.X) > SystemInformation.DragSize.Width ||
                 Math.Abs(e.Y - _dragStart.Y) > SystemInformation.DragSize.Height))
            {
                _panning = true;
                Cursor = Cursors.SizeAll;
            }
            if (_panning)
            {
                var vr = VideoRectF;
                _center = new PointF(_dragCenter.X - (e.X - _dragStart.X) / vr.Width, _dragCenter.Y - (e.Y - _dragStart.Y) / vr.Height);
                ClampCenter();
                Invalidate();
            }
            return; // dragging the video never brings the overlay back
        }

        if (moved) Activity();
        var h = HitTest(e.Location);
        if (h != _hover)
        {
            // Only repaint the affected controls (seek hover changes the bar, so repaint the whole panel then).
            InvalidateHit(_hover);
            InvalidateHit(h);
            _hover = h;
        }
        Cursor = h != Hit.None ? Cursors.Hand : _zoom > 1f ? Cursors.SizeAll : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != Hit.None) { _hover = Hit.None; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var h = HitTest(e.Location);
        if (h != Hit.None) Activity();
        if (h == Hit.Gamma && _ser != null)
        {
            _brightDragging = true;
            Capture = true;
            BrightnessTo(e.X);
            return;
        }
        if (h == Hit.Seek && _ser != null)
        {
            _seeking = true;
            Capture = true;
            SeekTo(e.X);
            return;
        }
        _pressed = h;
        if (h == Hit.None)
        {
            if (e.Clicks == 2)
            {
                TogglePlay(); // undo the first click's play/pause toggle
                ToggleFullscreen();
                _ignoreUp = true;
                return;
            }
            _dragArmed = true;
            _dragStart = e.Location;
            _dragCenter = _center;
            Capture = true;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_brightDragging)
        {
            _brightDragging = false;
            Capture = false;
            Invalidate();
            return;
        }
        if (_seeking)
        {
            _seeking = false;
            Capture = false;
            if (_playing) RestartClock();
            Invalidate();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (_dragArmed)
        {
            bool panned = _panning;
            _dragArmed = _panning = false;
            Capture = false;
            Cursor = _zoom > 1f ? Cursors.SizeAll : Cursors.Default;
            _lastMousePos = PointToScreen(e.Location); // don't treat the release as movement
            if (panned) { _pressed = Hit.None; return; }
        }
        if (_ignoreUp) { _ignoreUp = false; return; }
        var h = HitTest(e.Location);
        if (h != _pressed) return;
        _pressed = Hit.None;
        switch (h)
        {
            case Hit.None: if (_ser != null) TogglePlay(); break;
            case Hit.Center: if (_ser == null) OpenDialog(); else TogglePlay(); break;
            case Hit.Play: TogglePlay(); break;
            case Hit.Rew: Jump(-SkipSeconds); break;
            case Hit.Fwd: Jump(+SkipSeconds); break;
            case Hit.Loop: if (_ser != null) { _loop = !_loop; Invalidate(); } break;
            case Hit.Bright: if (_ser != null) SetBrightness(0.5f); break; // click the icon = reset, like mute
            case Hit.Full: ToggleFullscreen(); break;
            case Hit.More: ShowMenu(); break;
            case Hit.Min: WindowState = FormWindowState.Minimized; break;
            case Hit.Max: ToggleMaximize(); break;
            case Hit.Close: Close(); break;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_ser == null) return;
        // Ctrl+wheel = zoom at the cursor. Precision-touchpad pinch also arrives this way.
        if ((ModifierKeys & Keys.Control) != 0)
        {
            ZoomAt((float)Math.Pow(1.2, e.Delta / 120.0), e.Location);
            return;
        }
        if (HitTest(e.Location) is Hit.Gamma or Hit.Bright)
        {
            Activity();
            SetBrightness(_bright + 0.05f * e.Delta / 120f);
            return;
        }
        Step(e.Delta > 0 ? -1 : +1);
    }

    void InvalidateHit(Hit h)
    {
        if (h == Hit.None) return;
        var r = h switch
        {
            Hit.Seek => PanelRect,
            Hit.Min or Hit.Max or Hit.Close => WindowButtonsRect,
            _ => Rect(h),
        };
        r.Inflate(P(4), P(4));
        Invalidate(r);
    }

    void BrightnessTo(int x)
    {
        var r = Rect(Hit.Gamma);
        float l = r.Left + P(6), w = r.Width - P(12);
        SetBrightness((x - l) / w);
    }

    void SeekTo(int x)
    {
        if (_ser == null) return;
        var sr = SeekRect;
        float frac = Math.Clamp((x - sr.Left) / (float)sr.Width, 0f, 1f);
        ShowFrame((int)Math.Round(frac * (_ser.FrameCount - 1)));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            // Zoom (doesn't bring the overlay back)
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Shift | Keys.Oemplus:
            case Keys.Control | Keys.Add:
                ZoomCentered(1.25f); return true;
            case Keys.Control | Keys.OemMinus:
            case Keys.Control | Keys.Subtract:
                ZoomCentered(1 / 1.25f); return true;
            case Keys.Control | Keys.D0:
            case Keys.Control | Keys.NumPad0:
                ResetZoom(); return true;
        }
        Activity();
        switch (keyData)
        {
            case Keys.Up: SetBrightness(_bright + 0.05f); return true;
            case Keys.Down: SetBrightness(_bright - 0.05f); return true;
            case Keys.Left: Step(-1); return true;
            case Keys.Right: Step(+1); return true;
            case Keys.Space: TogglePlay(); return true;
            case Keys.Home: Pause(); ShowFrame(0); return true;
            case Keys.End: Pause(); ShowFrame(int.MaxValue); return true;
            case Keys.PageUp: case Keys.J: Jump(-SkipSeconds); return true;
            case Keys.PageDown: case Keys.L: Jump(+SkipSeconds); return true;
            case Keys.Control | Keys.O: OpenDialog(); return true;
            case Keys.B: ToggleByteOrder(); return true;
            case Keys.F11: case Keys.F: ToggleFullscreen(); return true;
            case Keys.Escape when _fullscreen: ToggleFullscreen(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ───────────────────────── menu ─────────────────────────

    void BuildMenu()
    {
        _menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false };
        _menu.ShowImageMargin = false;
        _menu.ForeColor = Color.Gainsboro;
        _menu.Font = new Font("Segoe UI", 9.5f);
        _menu.Opening += (_, _) => PopulateMenu();
        _menu.Closed += (_, _) => Activity();
    }

    void PopulateMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem("Open file…", null, (_, _) => OpenDialog()) { ShortcutKeyDisplayString = "Ctrl+O" });

        // Debayer preview (SharpCap-style)
        var debayer = new ToolStripMenuItem("Debayer preview")
        {
            Enabled = _ser is { IsSinglePlane: true },
            ToolTipText = _ser is { IsSinglePlane: false } ? "This file is already RGB" : null,
        };
        debayer.DropDown.BackColor = Color.FromArgb(32, 34, 40);
        string header = _ser == null ? "" : SerFile.PatternName(_ser.ColorId);
        ToolStripMenuItem Mode(int? mode, string label) =>
            new((_debayer == mode ? "✓  " : "     ") + label, null, (_, _) => SetDebayer(mode)) { ForeColor = Color.Gainsboro };
        debayer.DropDownItems.Add(Mode(null, header.Length > 0 ? $"Auto ({header})" : "Auto (mono)"));
        debayer.DropDownItems.Add(Mode(0, "Off (raw sensor data)"));
        debayer.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (id, name) in new[] { (8, "RGGB"), (9, "GRBG"), (10, "GBRG"), (11, "BGGR") })
            debayer.DropDownItems.Add(Mode(id, $"Force {name}"));
        if (debayer.DropDown is ToolStripDropDownMenu ddm) ddm.ShowImageMargin = false;
        _menu.Items.Add(debayer);

        var speed = new ToolStripMenuItem("Playback speed") { Enabled = _ser != null };
        speed.DropDown.BackColor = Color.FromArgb(32, 34, 40);
        var rates = new List<double> { 5, 10, 15, 25, 30, 50, 60, 100, 120, 200 };
        if (_ser?.DetectedFps is double det)
            speed.DropDownItems.Add(MakeRate(Math.Round(det, 1), $"{det:0.#} fps (recorded)"));
        foreach (var r in rates) speed.DropDownItems.Add(MakeRate(r, $"{r:0} fps"));
        if (speed.DropDown is ToolStripDropDownMenu dm) dm.ShowImageMargin = false;
        _menu.Items.Add(speed);

        _menu.Items.Add(new ToolStripMenuItem((_loop ? "✓  " : "     ") + "Loop", null, (_, _) => { _loop = !_loop; Invalidate(); }) { Enabled = _ser != null });
        _menu.Items.Add(new ToolStripMenuItem("Swap byte order (16-bit)", null, (_, _) => ToggleByteOrder())
            { Enabled = _ser is { BitDepth: > 8 }, ShortcutKeyDisplayString = "B" });
        _menu.Items.Add(new ToolStripMenuItem("Reset brightness", null, (_, _) => SetBrightness(0.5f)) { Enabled = ToneActive });
        _menu.Items.Add(new ToolStripMenuItem("Reset zoom", null, (_, _) => ResetZoom())
            { Enabled = _zoom > 1f, ShortcutKeyDisplayString = "Ctrl+0" });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(DefaultApp.IsDefault()
            ? new ToolStripMenuItem("✓  SER Player opens .ser files by default", null, (_, _) => MakeDefault())
            : new ToolStripMenuItem("     Open .ser files with SER Player by default…", null, (_, _) => MakeDefault()));
        _menu.Items.Add(new ToolStripMenuItem("     How to make SER Player the default…", null, (_, _) =>
        {
            RevealCursor();
            using var dlg = new DefaultAppDialog();
            dlg.ShowDialog(this);
        }));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Open crash log folder", null, (_, _) => CrashLog.OpenFolder()));
        _menu.Items.Add(new ToolStripMenuItem("Keyboard shortcuts", null, (_, _) =>
        {
            RevealCursor();
            using var dlg = new ShortcutsDialog(SkipSeconds);
            dlg.ShowDialog(this);
        }));
        _menu.Items.Add(new ToolStripMenuItem($"About {AppInfo.Name}", null, (_, _) =>
        {
            RevealCursor();
            using var dlg = new AboutDialog();
            dlg.ShowDialog(this);
        }));
    }

    /// <summary>Shows Windows' default-app picker for .ser (installing first if needed, so Windows has a stable path).</summary>
    void MakeDefault()
    {
        RevealCursor();
        if (!File.Exists(Installer.InstalledExe))
        {
            if (MessageBox.Show(this, "SER Player needs to be installed before Windows can use it as the default app for .ser files.\n\nInstall it now?",
                    "SER Player", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (Installer.InstallQuiet(InstallScope.CurrentUser) != 0)
            {
                MessageBox.Show(this, "Installation failed. See the crash log for details.", "SER Player", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        if (!DefaultApp.ShowPicker(this)) DefaultApp.OpenSettings();
    }

    ToolStripMenuItem MakeRate(double fps, string label)
    {
        bool sel = Math.Abs(fps - _fps) < 0.05;
        return new ToolStripMenuItem((sel ? "✓  " : "     ") + label, null, (_, _) => SetFps(fps)) { ForeColor = Color.Gainsboro };
    }

    void ShowMenu()
    {
        var r = Rect(Hit.More);
        _menu.Show(this, new Point(r.Right, r.Top - P(4)), ToolStripDropDownDirection.AboveLeft);
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(32, 34, 40), Hi = Color.FromArgb(58, 62, 72), Bd = Color.FromArgb(70, 74, 84);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Bd;
        public override Color MenuItemBorder => Hi;
        public override Color MenuItemSelected => Hi;
        public override Color MenuItemSelectedGradientBegin => Hi;
        public override Color MenuItemSelectedGradientEnd => Hi;
        public override Color SeparatorDark => Bd;
        public override Color SeparatorLight => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
    }

    // ───────────────────────── playback ─────────────────────────

    void OpenDialog()
    {
        RevealCursor();
        using var dlg = new OpenFileDialog { Filter = "SER video (*.ser)|*.ser|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) Open(dlg.FileName);
    }

    void Open(string path)
    {
        if (_busy > 0) { BeginInvoke(() => Open(path)); return; }
        SerFile ser;
        try { ser = new SerFile(path); }
        catch (Exception ex)
        {
            RevealCursor();
            MessageBox.Show(this, $"Could not open file:\n{path}\n\n{ex.Message}", "SER Player", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Pause();
        _ser?.Dispose();
        _bmp?.Dispose();
        _ser = ser;
        _bmp = new Bitmap(ser.Width, ser.Height, PixelFormat.Format32bppPArgb);
        _fps = ser.DetectedFps is double fps ? Math.Clamp(fps, 1, 10000) : 30;
        Text = $"{Path.GetFileName(path)} — SER Player";
        _blur?.Dispose();
        _blur = null;
        ser.DebayerOverride = _debayer;
        ResetZoom();
        _frame = -1;
        ShowFrame(0);
        Activity();
    }

    void ShowFrame(int index)
    {
        if (_ser == null || _bmp == null || _busy > 0) return;
        index = Math.Clamp(index, 0, _ser.FrameCount - 1);
        if (index != _frame)
        {
            Exception? error = null;
            _busy++;
            try { _ser.ReadFrame(index, _bmp); }
            catch (Exception ex) { error = ex; }
            finally { EndBusy(); }
            if (error != null)
            {
                Pause();
                CrashLog.Write(error, $"Frame read error (frame {index})");
                RevealCursor();
                MessageBox.Show(this, $"{error.Message}\n\nDetails saved to {CrashLog.FilePath}", "Read error");
                return;
            }
            _frame = index;
            _frameVersion++;
            _blurDirty = true;
        }
        Invalidate();
    }

    string ColorDescription()
    {
        if (_ser == null) return "";
        if (!_ser.IsSinglePlane || _ser.DebayerOverride == null) return _ser.ColorName;
        return _ser.DebayerOverride == 0
            ? $"{_ser.ColorName} (debayer off)"
            : $"Bayer {SerFile.PatternName(_ser.EffectiveColorId)} (forced)";
    }

    void SetDebayer(int? mode)
    {
        if (_busy > 0) return;
        _debayer = mode;
        if (_ser is not { IsSinglePlane: true }) { Invalidate(); return; }
        _ser.DebayerOverride = mode;
        int f = _frame;
        _frame = -1;
        ShowFrame(f);
    }

    void ToggleByteOrder()
    {
        if (_busy > 0) return;
        if (_ser is not { BitDepth: > 8 }) return;
        _ser.BigEndian = !_ser.BigEndian;
        int f = _frame;
        _frame = -1;
        ShowFrame(f);
    }

    void SetFps(double fps)
    {
        _fps = fps;
        if (_playing) RestartClock();
        Invalidate();
    }

    void TogglePlay()
    {
        if (_playing) Pause(); else Play();
        Activity(); // a play/pause action shows the controls briefly, then they fade like any other time
    }

    void Play()
    {
        if (_ser == null) return;
        if (_frame >= _ser.FrameCount - 1) ShowFrame(0);
        _playing = true;
        RestartClock();
        Activity(); // brief grace period before the overlay fades, like other players
        Invalidate();
    }

    void Pause()
    {
        _playing = false;
        _clock.Stop();
        Invalidate();
    }

    void RestartClock()
    {
        _playStartFrame = _frame;
        _clock.Restart();
    }

    void Step(int delta)
    {
        Pause();
        ShowFrame(_frame + delta);
        Update(); // paint now, so a held arrow key shows every frame instead of waiting behind key-repeat messages
    }

    void Jump(int seconds)
    {
        if (_ser == null) return;
        ShowFrame(_frame + (int)Math.Round(_fps * seconds));
        if (_playing) RestartClock();
    }

    void Tick()
    {
        if (_busy > 0) return;
        if (_ser != null && _playing && !_seeking)
        {
            int target = _playStartFrame + (int)(_clock.Elapsed.TotalSeconds * _fps);
            if (target >= _ser.FrameCount)
            {
                if (_loop) { ShowFrame(0); RestartClock(); }
                else { ShowFrame(_ser.FrameCount - 1); Pause(); Activity(); } // reached the end: show the controls briefly
            }
            else if (target != _frame) ShowFrame(target);
        }

        // Overlay visibility (same rules playing or paused):
        //  • shown after real mouse movement (not video dragging) or a play/pause action, hidden again after
        //    HideAfterSeconds idle, or immediately when the mouse leaves the window
        //  • always shown with no file open (the add-video button is the way in), while seeking, or with the menu open
        //  • stays up while the mouse rests on the (still visible) panel or window buttons
        //  • a loop restart does nothing to it
        var mouse = PointToClient(MousePosition);
        // Over this window itself, not over a dialog, menu or other window that happens to sit on top of it.
        bool mouseInside = ClientRectangle.Contains(mouse) && WindowFromPoint(MousePosition) == Handle;
        if (_ser != null && !mouseInside && !_seeking && !_menu.Visible) HideOverlayNow();
        bool restingOnUi = _ui > 0f && !_panning && (PanelRect.Contains(mouse) || WindowButtonsRect.Contains(mouse));
        bool show = _ser == null || _seeking || _menu.Visible || restingOnUi ||
                    _uptime.Elapsed.TotalSeconds - _lastActivity < HideAfterSeconds;
        float target2 = show ? 1f : 0f;
        if (_ui != target2)
        {
            _ui = show ? Math.Min(1f, _ui + 0.2f) : Math.Max(0f, _ui - 0.08f);
            if (_ui == 0f && _hover != Hit.None) _hover = Hit.None;
            Invalidate();
        }
        // Cursor.Hide() hides the pointer for every window of the app (dialogs included), so only while this window is
        // the active one and the pointer is over it.
        bool hideCursor = _ui == 0f && mouseInside && !_dragArmed && ActiveForm == this;
        if (hideCursor != _cursorHidden)
        {
            _cursorHidden = hideCursor;
            if (hideCursor) Cursor.Hide(); else Cursor.Show();
        }
    }

    /// <summary>Undoes the idle cursor hiding right away (before a dialog opens over this window).</summary>
    void RevealCursor()
    {
        if (!_cursorHidden) return;
        _cursorHidden = false;
        Cursor.Show();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        _ser?.Dispose();
        _bmp?.Dispose();
        _blur?.Dispose();
        _view?.Dispose();
        base.OnFormClosed(e);
    }

    // ───────────────────────── interop ─────────────────────────

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)] struct POINTS { public short x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct GESTUREINFO
    {
        public int cbSize, dwFlags, dwID;
        public IntPtr hwndTarget;
        public POINTS ptsLocation;
        public int dwInstanceID, dwSequenceID;
        public ulong ullArguments;
        public int cbExtraArgs;
    }
    [StructLayout(LayoutKind.Sequential)] struct GESTURECONFIG { public int dwID, dwWant, dwBlock; }

    [DllImport("user32.dll")] static extern bool GetGestureInfo(IntPtr hGestureInfo, ref GESTUREINFO pGestureInfo);
    [DllImport("user32.dll")] static extern bool CloseGestureInfoHandle(IntPtr hGestureInfo);
    [DllImport("user32.dll")] static extern bool SetGestureConfig(IntPtr hwnd, int dwReserved, int cIDs, GESTURECONFIG[] pGestureConfig, int cbSize);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point pt);
}
