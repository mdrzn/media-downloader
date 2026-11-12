// Icone disegnate nel codice: riquadro arrotondato col colore dello stato, freccia in giu' su un vassoio.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MediaDownloader;

enum State { Ready, Downloading, Transcribing, Paused, Error }

static class Icons
{
    public static Color ColorOf(State s) => s switch
    {
        State.Downloading => Color.FromArgb(0xF0, 0x8A, 0x24),
        State.Transcribing => Color.FromArgb(0x8B, 0x5C, 0xF6),
        State.Paused => Color.FromArgb(0x8C, 0x91, 0x99),
        State.Error => Color.FromArgb(0xD9, 0x34, 0x2B),
        _ => Color.FromArgb(0x25, 0x82, 0xEB),
    };

    static readonly int[] traySizes = [16, 20, 24, 32, 40, 48, 64];

    public static Icon Tray(State s)
    {
        var c = ColorOf(s);
        return FromIco(Ico(traySizes.Select(px => Draw(px, Lighter(c), c, margin: false))), SystemInformation.SmallIconSize);
    }

    public static Icon App() => FromIco(Ico(new[] { 16, 24, 32, 48, 64 }.Select(AppBitmap)), new Size(32, 32));

    public static void ExportAppIcon(string path) =>
        File.WriteAllBytes(path, Ico(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }.Select(AppBitmap)));

    static Bitmap AppBitmap(int px) => Draw(px, Color.FromArgb(0x4F, 0x9C, 0xF9), Color.FromArgb(0x6D, 0x3F, 0xE0), margin: px >= 32);

    public static Bitmap Dot(Color c, int px)
    {
        var b = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(c);
        var d = px * 0.55f;
        g.FillEllipse(brush, (px - d) / 2, (px - d) / 2, d, d);
        return b;
    }

    static Bitmap Draw(int px, Color top, Color bottom, bool margin)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float m = margin ? px * 0.08f : 0, size = px - 2 * m;
        var box = new RectangleF(m, m, size, size);
        using (var path = Rounded(box, size * 0.22f))
        using (var fill = new LinearGradientBrush(box, top, bottom, LinearGradientMode.Vertical))
            g.FillPath(fill, path);

        PointF P(float x, float y) => new(m + x * size, m + y * size);
        using var pen = new Pen(Color.White, Math.Max(1.6f, size * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        // freccia in giu'
        g.DrawLine(pen, P(0.5f, 0.17f), P(0.5f, 0.6f));
        g.DrawLines(pen, [P(0.31f, 0.43f), P(0.5f, 0.62f), P(0.69f, 0.43f)]);
        // vassoio
        g.DrawLines(pen, [P(0.2f, 0.62f), P(0.2f, 0.8f), P(0.8f, 0.8f), P(0.8f, 0.62f)]);
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Color Lighter(Color c) => Color.FromArgb(Math.Min(255, c.R + 28), Math.Min(255, c.G + 28), Math.Min(255, c.B + 28));

    // Icone degli stati con la didascalia, per il README
    public static void SaveStates(string path, IEnumerable<(State state, string label)> states)
    {
        const int icon = 48, cell = 150, height = 96;
        var list = states.ToList();
        using var bmp = new Bitmap(cell * list.Count, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 15f, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.FromArgb(0x80, 0x86, 0x8E));
        using var center = new StringFormat { Alignment = StringAlignment.Center };
        for (int i = 0; i < list.Count; i++)
        {
            var c = ColorOf(list[i].state);
            using var b = Draw(icon, Lighter(c), c, margin: false);
            g.DrawImage(b, i * cell + (cell - icon) / 2, 10);
            g.DrawString(list[i].label, font, text, new RectangleF(i * cell, 66, cell, 24), center);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    // File .ico con le immagini in PNG
    static byte[] Ico(IEnumerable<Bitmap> images)
    {
        var pngs = images.Select(b => { using (b) { var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); return (b.Width, data: ms.ToArray()); } }).ToList();
        using var o = new MemoryStream();
        using var w = new BinaryWriter(o);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)pngs.Count);
        var offset = 6 + 16 * pngs.Count;
        foreach (var (px, data) in pngs)
        {
            w.Write((byte)(px >= 256 ? 0 : px)); w.Write((byte)(px >= 256 ? 0 : px));
            w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in pngs) w.Write(data);
        return o.ToArray();
    }

    static Icon FromIco(byte[] ico, Size size)
    {
        using var ms = new MemoryStream(ico);
        return new Icon(ms, size);
    }
}

// Avvio automatico: HKCU\…\Run, nessun permesso di amministratore
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "MediaDownloader";
    static string Exe => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool Enabled
    {
        get => Store.Demo || Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(Name) is string v
               && v.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (Store.Demo) return;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(Name, $"\"{Exe}\"");
            else key.DeleteValue(Name, false);
        }
    }
}
