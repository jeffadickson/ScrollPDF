using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Channels;

namespace ScrollPDF;

// Independent capture and disk writer. No image matching in this path.
public sealed class RawFrameRecorder
{
    private readonly Rectangle area;
    private readonly string folder;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationTokenSource writerFailed = new();
    private readonly Channel<(long Id, Bitmap Frame, DateTimeOffset Utc, long Ticks)> channel;
    public int QueueCapacity { get; }
    public long MaximumGapMilliseconds => Interlocked.Read(ref maxGapMs);
    private long maxGapMs, previousCaptureTicks;
    private long backpressureEvents;
    public long BackpressureEvents => Interlocked.Read(ref backpressureEvents);
    private Task? running;
    private long captured, saved;
    public const int TargetFps = 20;
    public double ActualFps => Captured / Math.Max(.001, stopwatch.Elapsed.TotalSeconds);
    private readonly Stopwatch stopwatch = new();
    public long Captured => Interlocked.Read(ref captured);
    public long Saved => Interlocked.Read(ref saved);
    public int QueueSize => channel.Reader.Count;
    public string Folder => folder;
    public bool Finished => running?.IsCompleted ?? false;
    public Task Completion => running ?? Task.CompletedTask;

    public RawFrameRecorder(Rectangle area, string folder)
    {
        this.area = area; this.folder = folder;
        // Target up to 512 MiB of uncompressed 24-bit pixel buffers, capped at
        // 512 frames. At 1008x702 this allows ~253 frames versus the old 16.
        long frameBytes = checked((long)area.Width * area.Height * 3);
        QueueCapacity = (int)Math.Clamp((512L * 1024 * 1024) / frameBytes, 16, 512);
        channel = Channel.CreateBounded<(long, Bitmap, DateTimeOffset, long)>(
            new BoundedChannelOptions(QueueCapacity)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    }

    public Task StartAsync()
    {
        if (running != null) throw new InvalidOperationException("Already running");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException("Recording directory is not empty");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "region.txt"), $"{area.X},{area.Y},{area.Width},{area.Height}");
        File.WriteAllText(Path.Combine(folder, "frames.csv"),"frame,utc,monotonic_ticks,gap_ms,file"+Environment.NewLine);
        stopwatch.Start();
        running = RunAsync();
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        var writer = Task.Run(WriteAsync);
        var producer = Task.Run(CaptureAsync);
        _ = writer.ContinueWith(t =>
        {
            if (t.IsFaulted) { writerFailed.Cancel(); stop.Cancel(); }
        }, TaskContinuationOptions.ExecuteSynchronously);
        await Task.WhenAll(producer, writer);
        File.WriteAllText(Path.Combine(folder, "completed.txt"),
            $"Captured={Captured}\nSaved={Saved}\nMaxGapMs={MaximumGapMilliseconds}\nBackpressureEvents={BackpressureEvents}\nQueueCapacity={QueueCapacity}\n");
    }

    private async Task CaptureAsync()
    {
        long n = 0;
        long frameNumber = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token,writerFailed.Token);
        var token = linked.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
                try
                {
                    using(var graphics = Graphics.FromImage(bmp))
                        graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
                    long ticks = Stopwatch.GetTimestamp();
                    var utc = DateTimeOffset.UtcNow;
                    long prev = Interlocked.Exchange(ref previousCaptureTicks, ticks);
                    if (prev != 0)
                    {
                        long gapMs = (long)Math.Round((ticks - prev) * 1000.0 / Stopwatch.Frequency);
                        long old;
                        while (gapMs > (old = Interlocked.Read(ref maxGapMs)) &&
                               Interlocked.CompareExchange(ref maxGapMs, gapMs, old) != old) { }
                    }
                    if (!channel.Writer.TryWrite((n,bmp,utc,ticks)))
                    {
                        Interlocked.Increment(ref backpressureEvents);
                        await channel.Writer.WriteAsync((n,bmp,utc,ticks), token);
                    }
                }
                catch { bmp.Dispose(); throw; }
                n++; Interlocked.Increment(ref captured);
                frameNumber++;
                var nextFrame = TimeSpan.FromTicks(frameNumber * TimeSpan.TicksPerSecond / TargetFps);
                var delay = nextFrame - stopwatch.Elapsed;
                try { if (delay > TimeSpan.Zero) await Task.Delay(delay, token); }
                catch(OperationCanceledException) when(token.IsCancellationRequested) { break; }
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        finally { channel.Writer.TryComplete(); }
    }

    private async Task WriteAsync()
    {
        await foreach(var item in channel.Reader.ReadAllAsync())
            using(item.Frame)
            {
                var path = Path.Combine(folder,$"frame-{item.Id:D7}.png");
                item.Frame.Save(path,ImageFormat.Png);
                File.AppendAllText(Path.Combine(folder,"frames.csv"),
                    $"{item.Id},{item.Utc:O},{item.Ticks},,{Path.GetFileName(path)}"+Environment.NewLine);
                Interlocked.Increment(ref saved);
            }
    }

    public async Task StopAsync()
    {
        stop.Cancel();
        if(running != null) await running;
        stopwatch.Stop();
        stop.Dispose();
        writerFailed.Dispose();
    }
}
