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
    public static int FindScrollDelta(Bitmap previous, Bitmap current, int maximumShift)
    {
        int w = Math.Min(previous.Width, current.Width);
        int h = Math.Min(previous.Height, current.Height);
        int max = Math.Min(Math.Max(0, maximumShift), h - 24);
        if (w < 40 || max < 2) return 0;
        int stepX = Math.Max(1, w / 72), stepY = 5;
        double best = double.MaxValue, zero = Difference(previous, current, 0, w, h, stepX, stepY);
        int bestShift = 0;
        for (int shift = 2; shift <= max; shift += 3)
        {
            double err = Difference(previous, current, shift, w, h, stepX, stepY);
            if (err < best) { best = err; bestShift = shift; }
        }
        int approx = bestShift;
        for (int s = Math.Max(1, approx - 3); s <= Math.Min(max, approx + 3); s++)
        {
            double err = Difference(previous, current, s, w, h, stepX, stepY);
            if (err < best) { best = err; bestShift = s; }
        }
        if (bestShift < 2 || best > 20 || (zero - best) < 3.5) return 0;
        return bestShift;
    }
    private static double Difference(Bitmap a, Bitmap b, int shift, int w, int h, int stepX, int stepY)
    {
        long difference = 0, count = 0;
        for (int y = 6; y < h - shift - 6; y += stepY)
            for (int x = Math.Max(0, w / 20); x < w - w / 20; x += stepX)
            {
                var ca = a.GetPixel(x, y + shift);
                var cb = b.GetPixel(x, y);
                difference += Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);
                count += 3;
            }
        return count == 0 ? double.MaxValue : (double)difference / count;
    }
    public static Bitmap Crop(Bitmap bmp, Rectangle r)
    {
        var dest = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dest);
        g.DrawImage(bmp, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        return dest;
    }
}