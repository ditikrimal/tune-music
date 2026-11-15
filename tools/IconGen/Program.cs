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
    // Tune logo: a white sound waveform (six rounded bars), no tile — reads cleanly on the dark taskbar and sidebar.
    var v = new DrawingVisual();
    using (var dc = v.RenderOpen())
    {
        var white = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE4, 0xE4, 0xEA), 90);
        if (size >= 32)
        {
            // design grid 24×24: x centre, top, bottom (stroke 2.2, round caps)
            (double x, double y1, double y2)[] bars = [(2, 10, 13), (6, 6, 17), (10, 3, 21), (14, 8, 15), (18, 5, 18), (22, 10, 13)];
            double pad = size * 0.06, u = (size - 2 * pad) / 24.0, w = 2.4 * u;
            foreach (var (x, y1, y2) in bars)
                dc.DrawRoundedRectangle(white, null, new Rect(pad + x * u - w / 2, pad + y1 * u - w / 2, w, (y2 - y1) * u + w), w / 2, w / 2);
        }
        else
        {
            // tiny sizes: four bars snapped to whole pixels
            double w = Math.Max(2, Math.Round(size / 8.0)), gap = Math.Max(2, Math.Round(size / 8.0));
            double total = 4 * w + 3 * gap, x0 = Math.Round((size - total) / 2);
            (double top, double bottom)[] hs = [(0.30, 0.70), (0.10, 0.90), (0.36, 0.64), (0.22, 0.78)];
            for (var k = 0; k < 4; k++)
            {
                double top = Math.Round(size * hs[k].top), bottom = Math.Round(size * hs[k].bottom);
                dc.DrawRoundedRectangle(Brushes.White, null, new Rect(x0 + k * (w + gap), top, w, bottom - top), w / 2, w / 2);
            }
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
