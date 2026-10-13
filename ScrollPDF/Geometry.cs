using System.Drawing;
using System.Drawing.Imaging;
namespace ScrollPDF;
public static class Geometry
{
    public static (int start, int end)? FindPageBreak(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 80 || h < 14) return null;
        int x0 = w / 5, x1 = w * 4 / 5;
        bool[] gray = new bool[h];
        bool[] white = new bool[h];
        for (int y = 0; y < h; y++)
        {
            int mid = 0, bright = 0, sampled = 0;
            for (int x = x0; x < x1; x += Math.Max(1, (x1 - x0) / 96))
            {
                var c = bmp.GetPixel(x, y);
                int maximum = Math.Max(c.R, Math.Max(c.G, c.B));
                int minimum = Math.Min(c.R, Math.Min(c.G, c.B));
                if (maximum - minimum <= 12 && maximum >= 85 && maximum <= 235) mid++;
                if (minimum >= 245) bright++;
                sampled++;
            }
            gray[y] = mid * 100 >= sampled * 88;
            white[y] = bright * 100 >= sampled * 88;
        }
        for (int y = 2; y < h - 6;)
        {
            if (!gray[y]) { y++; continue; }
            int s = y;
            while (y < h && gray[y]) y++;
            int e = y;
            if (e - s < 4) continue;
            bool before = false, after = false;
            for (int i = Math.Max(0, s - 9); i < s; i++) before |= white[i];
            for (int i = e; i < Math.Min(h, e + 9); i++) after |= white[i];
            if (before && after) return (s, e);
        }
        return null;
    }
    /// <summary>
    /// Match successive captures using textured central columns and coarse-to-fine vertical registration.
    /// Returns zero if alignment is ambiguous; a zero result must not be interpreted as proof
    /// that the screen has not moved.
    /// </summary>
    public static int FindScrollDelta(Bitmap previous, Bitmap current, int maximumShift)
        => RegisterScroll(previous, current, maximumShift).Delta;

    public readonly record struct ScrollMatch(int Delta, double Error, double Confidence, string Reason)
    {
        public bool Accepted => Delta > 0;
    }

    public static ScrollMatch RegisterScroll(Bitmap previous, Bitmap current, int maximumShift)
    {
        int w = Math.Min(previous.Width, current.Width);
        int h = Math.Min(previous.Height, current.Height);
        int maxShift = Math.Min(Math.Max(0, maximumShift), (int)(h * .70));
        if (w < 80 || h < 100 || maxShift < 3)
            return new(0, double.PositiveInfinity, 0, "Capture region too small");

        // Sample only the central content region, avoiding fixed toolbars and scrollbars.
        // Use a deterministic rectangular grid rather than per-candidate GetPixel calls.
        const int nx = 56;
        int stepY = Math.Max(2, h / 180);
        int ny = (h + stepY - 1) / stepY;
        byte[,] a = new byte[ny, nx], b = new byte[ny, nx];
        int[] xs = new int[nx];
        for (int x = 0; x < nx; x++)
            xs[x] = w / 10 + x * (w * 8 / 10 - 1) / (nx - 1);
        for (int y = 0; y < ny; y++)
        {
            int yy = Math.Min(h - 1, y * stepY);
            for (int x = 0; x < nx; x++)
            {
                var p = previous.GetPixel(xs[x], yy);
                var q = current.GetPixel(xs[x], yy);
                a[y, x] = (byte)((p.R * 77 + p.G * 150 + p.B * 29) >> 8);
                b[y, x] = (byte)((q.R * 77 + q.G * 150 + q.B * 29) >> 8);
            }
        }

        // Compare the overlap of old frame shifted up against the new frame.
        // Scoring uses differences weighted toward informative (non-flat) regions.
        double Score(int delta)
        {
            int offset = (int)Math.Round((double)delta / stepY);
            int stop = ny - offset - 3;
            if (stop < ny / 4) return double.PositiveInfinity;
            double error = 0, weight = 0;
            for (int y = 2; y < stop; y += 2)
                for (int x = 2; x < nx - 2; x += 2)
                {
                    int v = a[y + offset, x], t = b[y, x];
                    int tex = Math.Max(Math.Abs(a[y + offset, x + 1] - a[y + offset, x - 1]),
                                       Math.Abs(a[y + offset + 1, x] - a[y + offset - 1, x]));
                    if (tex < 8) continue;
                    double wt = Math.Min(48, tex);
                    error += wt * Math.Abs(v - t);
                    weight += wt;
                }
            return weight < 500 ? double.PositiveInfinity : error / weight;
        }

        int bestShift = 0;
        double best = double.PositiveInfinity;
        for (int shift = 0; shift <= maxShift; shift += stepY * 3)
        {
            double score = Score(shift);
            if (score < best) { best = score; bestShift = shift; }
        }
        int rough = bestShift;
        for (int shift = Math.Max(0, rough - stepY * 4);
             shift <= Math.Min(maxShift, rough + stepY * 4); shift += stepY)
        {
            double score = Score(shift);
            if (score < best) { best = score; bestShift = shift; }
        }
        if (double.IsInfinity(best))
            return new(0, best, 0, "Insufficient textured overlap");
        // A small difference to the static frame is a genuine stationary capture.
        double still = Score(0);
        if (bestShift < stepY * 2 || still <= best + 1)
            return new(0, best, 1, "No measurable scroll");
        // A poor match must not generate invented missing/duplicated document content.
        double confidence = Math.Clamp((still - best) / Math.Max(1, still), 0, 1);
        if (best > 19 || confidence < .35)
            return new(0, best, confidence, "Ambiguous frame overlap");
        return new(bestShift, best, confidence, "Matched");
    }
    public static Bitmap Crop(Bitmap bmp, Rectangle r)
    {
        var dest = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dest);
        g.DrawImage(bmp, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        return dest;
    }
}