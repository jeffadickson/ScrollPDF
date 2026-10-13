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

    public enum MatchStatus { Stationary, Matched, Ambiguous }
    public readonly record struct ScrollMatch(MatchStatus Status, int Delta, double Error, double Confidence);
    public static ScrollMatch RegisterScroll(Bitmap previous, Bitmap current, int maximumShift)
    {
        var estimate = RegisterScrollCore(previous, current, maximumShift);
        if (estimate.Reason == "Matched")
            return new(MatchStatus.Matched, estimate.Delta, estimate.Error, estimate.Confidence);
        if (estimate.Reason == "No measurable scroll")
            return new(MatchStatus.Stationary, 0, estimate.Error, estimate.Confidence);
        return new(MatchStatus.Ambiguous, 0, estimate.Error, estimate.Confidence);
    }

    private readonly record struct ScrollEstimate(int Delta, double Error, double Confidence, string Reason)
    {
        public bool Accepted => Delta > 0;
    }

    private static ScrollEstimate RegisterScrollCore(Bitmap previous, Bitmap current, int maximumShift)
    {
        int w = Math.Min(previous.Width, current.Width);
        int h = Math.Min(previous.Height, current.Height);
        int maximum = Math.Min(Math.Max(0, maximumShift), (int)(h * .70));
        if (w < 80 || h < 100 || maximum < 3)
            return new(0, double.PositiveInfinity, 0, "Capture region too small");

        // Preserve EVERY vertical row. Previously the 2-3px vertical decimation
        // rounded candidate offsets and made fine chart grids alias together.
        // Sample central columns to exclude PDF viewer scrollbars and toolbars.
        const int columns = 64;
        var a = new byte[h, columns];
        var b = new byte[h, columns];
        for (int x = 0; x < columns; x++)
        {
            int xx = w / 10 + x * (w * 8 / 10 - 1) / (columns - 1);
            for (int y = 0; y < h; y++)
            {
                Color pa = previous.GetPixel(xx, y);
                Color pb = current.GetPixel(xx, y);
                a[y, x] = (byte)((pa.R * 77 + pa.G * 150 + pa.B * 29) >> 8);
                b[y, x] = (byte)((pb.R * 77 + pb.G * 150 + pb.B * 29) >> 8);
            }
        }

        double Score(int delta)
        {
            int remaining = h - delta;
            if (remaining < h * .30) return double.PositiveInfinity;
            double total = 0, weights = 0;
            // Every row is essential: periodic chart/grid lines alias if we
            // examine every third row. Also inspect gradients on BOTH images,
            // otherwise blank pixels can score as a false perfect match.
            for (int y = 2; y < remaining - 2; y++)
                for (int x = 2; x < columns - 2; x += 2)
                {
                    int sourceY = y + delta;
                    int oldValue = a[sourceY, x], newValue = b[y, x];
                    int oldGradient = Math.Max(
                        Math.Abs(a[sourceY - 1, x] - a[sourceY + 1, x]),
                        Math.Abs(a[sourceY, x - 1] - a[sourceY, x + 1]));
                    int newGradient = Math.Max(
                        Math.Abs(b[y - 1, x] - b[y + 1, x]),
                        Math.Abs(b[y, x - 1] - b[y, x + 1]));
                    int strength = Math.Max(oldGradient, newGradient);
                    if (strength < 6) continue;
                    double weight = Math.Min(strength, 64);
                    total += weight * (Math.Abs(oldValue - newValue) +
                                      Math.Abs(oldGradient - newGradient));
                    weights += weight;
                }
            return weights < 150 ? double.PositiveInfinity : total / weights;
        }

        // Exhaustive pixel-accurate search. Sampling already makes this cheap
        // at ordinary capture region dimensions. It avoids missing narrow
        // peaks in periodic charts during a coarse-to-fine candidate search.
        double best = double.PositiveInfinity;
        int bestShift = 0;
        double[] scores = new double[maximum + 1];
        for (int delta = 0; delta <= maximum; delta++)
        {
            double score = Score(delta);
            scores[delta] = score;
            if (score < best) { best = score; bestShift = delta; }
        }
        if (double.IsInfinity(best))
            return new(0, best, 0, "Insufficient textured overlap");

        double still = scores[0];
        // A frame that matches with no displacement is a stationary frame.
        if (bestShift <= 1 && still <= 2)
            return new(0, best, 1, "No measurable scroll");

        // Repeated grid lines can yield several plausible offsets. Require a
        // clear advantage over any competing shift outside the local ±2px peak.
        double second = double.PositiveInfinity;
        for (int i = 0; i < scores.Length; i++)
            if (Math.Abs(i - bestShift) > 2 && scores[i] < second) second = scores[i];

        double advantage = double.IsPositiveInfinity(second) ? 1 :
            Math.Clamp((second - best) / Math.Max(1, second), 0, 1);
        double improvement = double.IsPositiveInfinity(still) ? 1 :
            Math.Clamp((still - best) / Math.Max(1, still), 0, 1);
        double confidence = Math.Min(advantage, improvement);

        if (bestShift <= 1 && still <= best + 1)
            return new(0, best, 1, "No measurable scroll");
        // A uniquely exact match is decisive even for sparse line art.
        if (bestShift >= 2 && best <= 0.10 && second > 0.50)
            return new(bestShift, best, 1, "Matched");
        if (bestShift < 2 || best > 18 || confidence < .20)
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