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
    private readonly Channel<(long Id, Bitmap Frame)> channel =
        Channel.CreateBounded<(long, Bitmap)>(new BoundedChannelOptions(16)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private Task? running;
    private long captured, saved;
    public long Captured => Interlocked.Read(ref captured);
    public long Saved => Interlocked.Read(ref saved);
    public const int QueueCapacity = 16;
    public int QueueSize => channel.Reader.Count;
    public string Folder => folder;
    public bool Finished => running?.IsCompleted ?? false;
    public Task Completion => running ?? Task.CompletedTask;

    public RawFrameRecorder(Rectangle area, string folder) { this.area = area; this.folder = folder; }

    public Task StartAsync()
    {
        if (running != null) throw new InvalidOperationException("Already running");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException("Recording directory is not empty");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "region.txt"), $"{area.X},{area.Y},{area.Width},{area.Height}");
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
        File.WriteAllText(Path.Combine(folder, "completed.txt"), $"Captured={Captured}\nSaved={Saved}\n");
    }

    private async Task CaptureAsync()
    {
        long n = 0;
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
                    await channel.Writer.WriteAsync((n,bmp), token);
                }
                catch { bmp.Dispose(); throw; }
                n++; Interlocked.Increment(ref captured);
                try { await Task.Delay(125, token); }
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
                Interlocked.Increment(ref saved);
            }
    }

    public async Task StopAsync()
    {
        stop.Cancel();
        if(running != null) await running;
        stop.Dispose();
        writerFailed.Dispose();
    }
}
