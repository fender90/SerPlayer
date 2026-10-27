using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace SerPlayer;

/// <summary>Reader for the Lucam Recorder .SER video format (178-byte header, raw frames, optional timestamp trailer).</summary>
public sealed class SerFile : IDisposable
{
    const int HeaderSize = 178;

    readonly FileStream _fs;
    readonly byte[] _raw;
    byte[]? _px8;
    byte[]? _lut;
    readonly long[]? _timestamps;

    public string Path { get; }
    public int ColorId { get; }
    public int Width { get; }
    public int Height { get; }
    public int BitDepth { get; }
    public int FrameCount { get; }
    public string Observer { get; }
    public string Instrument { get; }
    public string Telescope { get; }
    public double? DetectedFps { get; }
    public bool BigEndian { get; set; }

    /// <summary>
    /// Debayer preview override for single-plane files (like SharpCap's): null = Auto (use the header's pattern),
    /// 0 = Off (show raw sensor data as mono), 8..11 = force RGGB / GRBG / GBRG / BGGR. Ignored for RGB/BGR files.
    /// </summary>
    public int? DebayerOverride { get; set; }

    public bool IsSinglePlane => ColorId < 100;

    /// <summary>The colour layout actually used for decoding, after the debayer override.</summary>
    public int EffectiveColorId => IsSinglePlane && DebayerOverride is int o ? o : ColorId;

    public static string PatternName(int colorId) => colorId switch
    {
        8 => "RGGB", 9 => "GRBG", 10 => "GBRG", 11 => "BGGR",
        16 => "CYYM", 17 => "YCMY", 18 => "YMCY", 19 => "MYYC",
        _ => "",
    };

    int Planes => ColorId >= 100 ? 3 : 1;
    int BytesPerSample => BitDepth > 8 ? 2 : 1;
    long FrameSize => (long)Width * Height * Planes * BytesPerSample;

    public SerFile(string path)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            var h = new byte[HeaderSize];
            _fs.ReadExactly(h);
            var id = Encoding.ASCII.GetString(h, 0, 14);
            if (!id.StartsWith("LUCAM-RECORDER", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Not a SER file (missing LUCAM-RECORDER signature).");

            ColorId = BitConverter.ToInt32(h, 18);
            int endianFlag = BitConverter.ToInt32(h, 22);
            Width = BitConverter.ToInt32(h, 26);
            Height = BitConverter.ToInt32(h, 30);
            BitDepth = BitConverter.ToInt32(h, 34);
            int headerFrames = BitConverter.ToInt32(h, 38);
            Observer = Str(h, 42); Instrument = Str(h, 82); Telescope = Str(h, 122);

            if (Width <= 0 || Height <= 0 || BitDepth < 1 || BitDepth > 16)
                throw new InvalidDataException($"Unsupported SER header ({Width}x{Height}, {BitDepth}-bit).");

            // Most capture software (FireCapture, SharpCap, ...) writes little-endian data regardless of the
            // flag's documented meaning; treat a non-zero flag as big-endian. Toggle with 'B' if it looks wrong.
            BigEndian = endianFlag != 0 && BitDepth > 8;

            long available = (_fs.Length - HeaderSize) / FrameSize;
            FrameCount = (int)Math.Min(Math.Max(headerFrames, 0), available);
            if (headerFrames <= 0) FrameCount = (int)available;
            if (FrameCount <= 0) throw new InvalidDataException("SER file contains no frames.");

            _raw = new byte[FrameSize];

            long trailer = HeaderSize + FrameCount * FrameSize;
            if (_fs.Length >= trailer + FrameCount * 8L)
            {
                var ts = new byte[FrameCount * 8];
                _fs.Position = trailer;
                _fs.ReadExactly(ts);
                _timestamps = new long[FrameCount];
                for (int i = 0; i < FrameCount; i++) _timestamps[i] = BitConverter.ToInt64(ts, i * 8);
                if (FrameCount > 1)
                {
                    double secs = (_timestamps[^1] - _timestamps[0]) / 1e7;
                    if (secs > 0)
                    {
                        double fps = (FrameCount - 1) / secs;
                        if (fps is > 0.1 and < 10000) DetectedFps = fps;
                    }
                }
            }
        }
        catch
        {
            _fs.Dispose();
            throw;
        }
    }

    static string Str(byte[] b, int off) => Encoding.ASCII.GetString(b, off, 40).TrimEnd('\0', ' ');

    public DateTime? FrameTimeUtc(int index)
    {
        if (_timestamps == null) return null;
        long t = _timestamps[index];
        if (t <= 0 || t >= DateTime.MaxValue.Ticks) return null;
        return new DateTime(t, DateTimeKind.Utc);
    }

    public string ColorName => ColorId switch
    {
        0 => "Mono",
        8 => "Bayer RGGB", 9 => "Bayer GRBG", 10 => "Bayer GBRG", 11 => "Bayer BGGR",
        16 => "Bayer CYYM", 17 => "Bayer YCMY", 18 => "Bayer YMCY", 19 => "Bayer MYYC",
        100 => "RGB", 101 => "BGR",
        _ => $"Color {ColorId}"
    };

    /// <summary>Decodes frame <paramref name="index"/> into <paramref name="bmp"/> (32bpp, same size as the video). Rows are processed in parallel.</summary>
    public unsafe void ReadFrame(int index, Bitmap bmp)
    {
        _fs.Position = HeaderSize + index * FrameSize;
        _fs.ReadExactly(_raw);

        int w = Width, h = Height, planes = Planes;
        int rowSamples = w * planes;
        var px8 = _px8 ??= new byte[rowSamples * h];

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            fixed (byte* rawP = _raw)
            fixed (byte* srcP = px8)
            fixed (byte* lutP = _lut ??= BuildLut())
            {
                IntPtr raw = (IntPtr)rawP, src8 = (IntPtr)srcP, lut = (IntPtr)lutP, dst = data.Scan0;
                int stride = data.Stride;

                // 1) Normalise to 8-bit samples (16-bit via lookup table: clamp + shift)
                if (BytesPerSample == 1) Buffer.BlockCopy(_raw, 0, px8, 0, px8.Length);
                else
                {
                    bool be = BigEndian;
                    NoPump.For(h, y =>
                    {
                        ushort* s = (ushort*)raw + (long)y * rowSamples;
                        byte* o = (byte*)src8 + (long)y * rowSamples;
                        byte* l = (byte*)lut;
                        if (be) for (int i = 0; i < rowSamples; i++) { ushort v = s[i]; o[i] = l[(ushort)((v >> 8) | (v << 8))]; }
                        else for (int i = 0; i < rowSamples; i++) o[i] = l[s[i]];
                    });
                }

                // 2) Convert to BGRA
                if (planes == 3)
                {
                    bool bgr = ColorId == 101;
                    NoPump.For(h, y =>
                    {
                        byte* s = (byte*)src8 + (long)y * rowSamples;
                        uint* row = (uint*)((byte*)dst + (long)y * stride);
                        for (int x = 0; x < w; x++, s += 3)
                            row[x] = bgr ? Pack(s[2], s[1], s[0]) : Pack(s[0], s[1], s[2]);
                    });
                }
                else if (EffectiveColorId is >= 8 and <= 11 && w >= 2 && h >= 2)
                {
                    // Position of R and B within each 2x2 cell
                    (int rx, int ry, int bx, int by) = EffectiveColorId switch
                    {
                        8 => (0, 0, 1, 1),  // RGGB
                        9 => (1, 0, 0, 1),  // GRBG
                        10 => (0, 1, 1, 0), // GBRG
                        _ => (1, 1, 0, 0),  // BGGR
                    };
                    // Bilinear demosaic
                    NoPump.For(h, y =>
                    {
                        byte* s = (byte*)src8;
                        uint* row = (uint*)((byte*)dst + (long)y * stride);
                        byte* up = s + (long)(y > 0 ? y - 1 : 1) * w;
                        byte* cur = s + (long)y * w;
                        byte* dn = s + (long)(y < h - 1 ? y + 1 : h - 2) * w;
                        int py = y & 1;
                        bool redRow = py == ry;
                        for (int x = 0; x < w; x++)
                        {
                            int xm = x > 0 ? x - 1 : 1, xp = x < w - 1 ? x + 1 : w - 2;
                            int px = x & 1;
                            int c = cur[x];
                            int r, g, b;
                            if (px == rx && py == ry)
                            {
                                r = c; g = (up[x] + dn[x] + cur[xm] + cur[xp]) >> 2; b = (up[xm] + up[xp] + dn[xm] + dn[xp]) >> 2;
                            }
                            else if (px == bx && py == by)
                            {
                                b = c; g = (up[x] + dn[x] + cur[xm] + cur[xp]) >> 2; r = (up[xm] + up[xp] + dn[xm] + dn[xp]) >> 2;
                            }
                            else
                            {
                                g = c;
                                int horiz = (cur[xm] + cur[xp]) >> 1, vert = (up[x] + dn[x]) >> 1;
                                if (redRow) { r = horiz; b = vert; } else { r = vert; b = horiz; }
                            }
                            row[x] = Pack(r, g, b);
                        }
                    });
                }
                else
                {
                    // Mono (and unsupported CMY patterns shown as raw mono)
                    NoPump.For(h, y =>
                    {
                        byte* s = (byte*)src8 + (long)y * w;
                        uint* row = (uint*)((byte*)dst + (long)y * stride);
                        for (int x = 0; x < w; x++) row[x] = Pack(s[x], s[x], s[x]);
                    });
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    static uint Pack(int r, int g, int b) => 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;

    byte[] BuildLut()
    {
        int shift = Math.Max(0, BitDepth - 8), maxVal = (1 << BitDepth) - 1;
        var lut = new byte[65536];
        for (int v = 0; v < lut.Length; v++) lut[v] = (byte)(Math.Min(v, maxVal) >> shift);
        return lut;
    }

    public void Dispose() => _fs.Dispose();
}
