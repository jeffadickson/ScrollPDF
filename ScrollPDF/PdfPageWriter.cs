using System.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ScrollPDF;

/// <summary>
/// Single-writer PDF assembler. Accumulates continuous strips before looking for
/// viewer gutters, including gutters that straddle two capture-strip boundaries.
/// </summary>
public sealed class PdfPageWriter : IDisposable
{
    private readonly PdfDocument pdf = new();
    private readonly string target;
    private readonly int width;
    private readonly int targetPixels;
    private Bitmap? pending;
    private bool finished;
    private int pagesWritten;
    public int PagesWritten => pagesWritten;
    public int BufferedPixels => pending?.Height ?? 0;

    public PdfPageWriter(string path, int width, int pageHeight = 0)
    {
        target = path;
        this.width = width;
        targetPixels = pageHeight > 0 ? pageHeight : (int)Math.Round(width * 11.0 / 8.5);
    }

    public void Append(Bitmap strip)
    {
        if (finished) throw new InvalidOperationException("Already finished");
        if (strip.Width != width) throw new ArgumentException("Strip width mismatch");
        // The lookbehind contains the footer of the previous strip while the
        // incoming region supplies the upper surface of the next source page.
        // Consequently gray gutters crossing an input-strip boundary are visible.
        using (var combined = new Bitmap(width, (pending?.Height ?? 0) + strip.Height))
        {
            using (var g = Graphics.FromImage(combined))
            {
                g.Clear(Color.White);
                if (pending != null) g.DrawImageUnscaled(pending, 0, 0);
                g.DrawImageUnscaled(strip, 0, pending?.Height ?? 0);
            }
            pending?.Dispose();
            pending = Geometry.Crop(combined, new Rectangle(0, 0, width, combined.Height));
        }
        ProcessPages();
    }

    private void ProcessPages()
    {
        while (pending != null && pending.Height > 0)
        {
            var gutter = Geometry.FindPageBreak(pending);
            if (gutter is { } boundary)
            {
                // Trim the gray viewer gutter completely from both document pages.
                // The source pages retain their existing margins/white space.
                Emit(0, boundary.start);
                ReplacePending(boundary.end);
                continue;
            }

            // Avoid arbitrary pagination in normal cases: allow room for a true
            // source boundary beyond a normal printed page height. This safety
            // fallback only applies to long continuous content with no gutter.
            int maximum = Math.Max(targetPixels * 2, targetPixels + 500);
            if (pending.Height > maximum)
            {
                Emit(0, targetPixels);
                ReplacePending(targetPixels);
                continue;
            }
            break;
        }
    }

    private void ReplacePending(int firstRow)
    {
        var old = pending!;
        var height = old.Height - firstRow;
        pending = height > 0 ? Geometry.Crop(old, new Rectangle(0, firstRow, width, height)) : null;
        old.Dispose();
    }

    private void Emit(int y, int height)
    {
        if (height <= 2 || pending is null) return;
        using var pageImage = Geometry.Crop(pending, new Rectangle(0, y, width, height));
        using var bytes = new MemoryStream();
        pageImage.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
        bytes.Position = 0;
        var page = pdf.AddPage();
        pagesWritten++;
        page.Size = PdfSharp.PageSize.Letter;
        using var graphics = XGraphics.FromPdfPage(page);
        using var image = XImage.FromStream(bytes);
        const double margin = 18;
        var scale = Math.Min((page.Width.Point - margin * 2) / width,
                             (page.Height.Point - margin * 2) / height);
        graphics.DrawImage(image, margin, margin, width * scale, height * scale);
    }

    public void Finish()
    {
        if (finished) return;
        if (pending != null)
        {
            Emit(0, pending.Height);
            pending.Dispose();
            pending = null;
        }
        if (pagesWritten == 0) throw new InvalidOperationException("No content captured");
        pdf.Save(target);
        finished = true;
    }

    public void Dispose() { pending?.Dispose(); pdf.Dispose(); }
}