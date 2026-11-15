using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Tune logo: the letter T built from equalizer bars, silver on a graphite tile (no colour accent).
// Writes a multi-size .ico (PNG entries) plus a 512 px PNG for docs.
// Small sizes use a simplified 3-bar T drawn on whole pixels so it stays crisp in the taskbar/tray.

var root = args.Length > 0 ? args[0] : FindRepoRoot();
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

// classic BMP (DIB) entries below 256 px — older Windows/GDI+ code reads small PNG entries as garbage
var pngs = sizes.Select(s => (Size: s, Png: s >= 256 ? Encode(Render(s)) : Dib(Render(s)))).ToList();
var icoPath = Path.Combine(root, "src", "Tune.App", "Assets", "Tune.ico");
Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
WriteIco(icoPath, pngs);
File.WriteAllBytes(Path.Combine(root, "docs", "logo.png"), Encode(Render(512)));
Console.WriteLine($"wrote {icoPath} ({new FileInfo(icoPath).Length / 1024} KB, sizes {string.Join(",", sizes)}) and docs/logo.png");

static BitmapSource Render(int size)
{
    var v = new DrawingVisual();
    using (var dc = v.RenderOpen())
    {
        double u = size / 512.0;                       // design grid is 512×512
        // dark mark: graphite tile with a soft top sheen, silver-white bars
        var tile = new LinearGradientBrush(Color.FromRgb(0x2C, 0x2C, 0x31), Color.FromRgb(0x08, 0x08, 0x0A), 90);
        var green = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x9C, 0x9C, 0xA6), 90); // bar fill
        dc.DrawRoundedRectangle(tile, null, new Rect(0, 0, size, size), 116 * u, 116 * u);
        if (size >= 48)
        {
            var rim = new Pen(new LinearGradientBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 90), Math.Max(1, 6 * u));
            var inset = rim.Thickness / 2;
            dc.DrawRoundedRectangle(null, rim, new Rect(inset, inset, size - 2 * inset, size - 2 * inset), 116 * u - inset, 116 * u - inset);
        }

        if (size >= 32)
        {
            // full mark: five bars, the middle one is the stem of the T
            (double x, double y, double h)[] bars = [(96, 150, 60), (160, 118, 124), (234, 96, 320), (308, 118, 124), (372, 150, 60)];
            foreach (var (x, y, h) in bars)
                dc.DrawRoundedRectangle(green, null, new Rect(x * u, y * u, 44 * u, h * u), 22 * u, 22 * u);
        }
        else
        {
            // tiny sizes: 3 bars snapped to whole pixels (bar width ≈ 1/6 of the icon)
            double w = Math.Max(2, Math.Round(size / 6.0));
            double gap = Math.Max(1, Math.Round(size / 16.0));
            double cx = Math.Round((size - w) / 2);
            double top = Math.Round(size * 0.19), stemBottom = Math.Round(size * 0.82);
            double sideTop = Math.Round(size * 0.27), sideBottom = Math.Round(size * 0.47);
            var r = w / 2;
            dc.DrawRoundedRectangle(green, null, new Rect(cx, top, w, stemBottom - top), r, r);
            dc.DrawRoundedRectangle(green, null, new Rect(cx - gap - w, sideTop, w, sideBottom - sideTop), r, r);
            dc.DrawRoundedRectangle(green, null, new Rect(cx + w + gap, sideTop, w, sideBottom - sideTop), r, r);
        }
    }
    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(v);
    return bmp;
}

static byte[] Encode(BitmapSource bmp)
{
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

// ICO-style DIB: BITMAPINFOHEADER (height doubled), bottom-up straight-alpha BGRA pixels, then an empty AND mask.
static byte[] Dib(BitmapSource bmp)
{
    int size = bmp.PixelWidth;
    var bgra = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
    var pixels = new byte[size * size * 4];
    bgra.CopyPixels(pixels, size * 4, 0);
    int maskStride = (size + 31) / 32 * 4;

    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(size); w.Write(size * 2);       // header size, width, height (XOR + AND)
    w.Write((short)1); w.Write((short)32);               // planes, bit count
    w.Write(0); w.Write(size * size * 4 + maskStride * size); // BI_RGB, image size
    w.Write(0); w.Write(0); w.Write(0); w.Write(0);       // resolution, colours
    for (int y = size - 1; y >= 0; y--) w.Write(pixels, y * size * 4, size * 4);
    w.Write(new byte[maskStride * size]);
    w.Flush();
    return ms.ToArray();
}

static void WriteIco(string path, List<(int Size, byte[] Png)> images)
{
    using var w = new BinaryWriter(File.Create(path));
    w.Write((short)0);
    w.Write((short)1);
    w.Write((short)images.Count);
    var offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((short)1);
        w.Write((short)32);
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) w.Write(png);
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "Tune.sln"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("run from inside the repo");
}
