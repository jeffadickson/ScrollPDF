using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Channels;
namespace ScrollPDF;

/// <summary>Four independently scheduled stages. The raw capture archive allows later replay.</summary>
public sealed class CapturePipeline
{
    private readonly Rectangle region;
    private readonly string output;
    private readonly Action<string> status;
    private readonly CancellationTokenSource captureStop = new();
    private readonly CancellationTokenSource failed = new();
    private readonly Channel<(long number, Bitmap bitmap)> frames = Channel.CreateBounded<(long, Bitmap)>(
        new BoundedChannelOptions(4) {SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly Channel<(long number, Bitmap bitmap)> raw = Channel.CreateBounded<(long, Bitmap)>(
        new BoundedChannelOptions(8) {SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly Channel<Bitmap> strips = Channel.CreateBounded<Bitmap>(
        new BoundedChannelOptions(12) {SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    private Task? work;
    private string? failureMessage;
    public string? FailureMessage => Volatile.Read(ref failureMessage);
    public bool IsFinished => work?.IsCompleted ?? false;
    public bool IsFaulted => work?.IsFaulted ?? false;
    public Task Completion => work ?? Task.CompletedTask;

    public long CapturedFrames => Interlocked.Read(ref captured);
    public long AnalyzedFrames => Interlocked.Read(ref analyzed);
    public long ArchivedFrames => Interlocked.Read(ref archived);
    public int PagesWritten => Volatile.Read(ref pagesWritten);
    public double LastConfidence => Volatile.Read(ref lastConfidence);
    public int LastDisplacement => Volatile.Read(ref lastDisplacement);
    private long captured, analyzed, archived;
    private int pagesWritten, lastDisplacement;
    private double lastConfidence;
    public string RawDirectory => Path.ChangeExtension(output,null) + ".frames";

    public CapturePipeline(Rectangle region,string output,Action<string> status)
    { this.region=region;this.output=output;this.status=status; }

    public Task StartAsync()
    {
        Directory.CreateDirectory(RawDirectory);
        work=RunAsync();
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        var workers=new Task[] { CaptureAsync(), ArchiveAsync(), AnalyzeAsync(), WriteAsync() };
        foreach(var task in workers)
            _=task.ContinueWith(t =>
            {
                if (!t.IsFaulted) return;
                var message = t.Exception?.GetBaseException().Message ?? "Unknown worker failure";
                Volatile.Write(ref failureMessage, message);
                try { status("CAPTURE FAILED: " + message); } catch { /* UI may be closing */ }
                failed.Cancel();captureStop.Cancel();
                frames.Writer.TryComplete(t.Exception);
                raw.Writer.TryComplete(t.Exception);
                strips.Writer.TryComplete(t.Exception);
            },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        await Task.WhenAll(workers);
    }

    private async Task CaptureAsync()
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(captureStop.Token,failed.Token);
        var token=linked.Token;
        long number=0;
        try
        {
            while(!token.IsCancellationRequested)
            {
                var bitmap=new Bitmap(region.Width,region.Height,PixelFormat.Format24bppRgb);
                try
                {
                    using(var graphics=Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(region.Location,Point.Empty,region.Size);
                    var archiveCopy=(Bitmap)bitmap.Clone();
                    try {await raw.Writer.WriteAsync((number,archiveCopy),token);}
                    catch {archiveCopy.Dispose();throw;}
                    await frames.Writer.WriteAsync((number,bitmap),token);
                    Interlocked.Increment(ref captured);
                    number++;
                }
                catch {bitmap.Dispose();throw;}
                await Task.Delay(125,token);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) {}
        finally {frames.Writer.TryComplete();raw.Writer.TryComplete();}
    }

    private async Task ArchiveAsync()
    {
        await foreach(var item in raw.Reader.ReadAllAsync(failed.Token))
        {
            using(item.bitmap)
                item.bitmap.Save(Path.Combine(RawDirectory,$"frame-{item.number:D7}.png"),ImageFormat.Png);
            Interlocked.Increment(ref archived);
        }
    }

    private async Task AnalyzeAsync()
    {
        Bitmap? previous=null;
        try
        {
            await foreach(var item in frames.Reader.ReadAllAsync(failed.Token))
            {
                Interlocked.Increment(ref analyzed);
                Bitmap current=item.bitmap;
                try
                {
                    if(previous is null)
                    {
                        var initial=Geometry.Crop(current,new Rectangle(0,0,current.Width,current.Height));
                        try {await strips.Writer.WriteAsync(initial,failed.Token);}
                        catch {initial.Dispose();throw;}
                    }
                    else
                    {
                        var match=Geometry.RegisterScroll(previous,current,(int)(current.Height*.66));
                        Volatile.Write(ref lastConfidence, match.Confidence);
                        Volatile.Write(ref lastDisplacement, match.Delta);
                        if(match.Status==Geometry.MatchStatus.Ambiguous)
                            throw new InvalidDataException($"Uncertain alignment at frame {item.number}; error {match.Error:F2}, confidence {match.Confidence:P0}. Raw frames retained in {RawDirectory}");
                        if(match.Status==Geometry.MatchStatus.Matched)
                        {
                            var tail=Geometry.Crop(current,new Rectangle(0,current.Height-match.Delta,current.Width,match.Delta));
                            try {await strips.Writer.WriteAsync(tail,failed.Token);}
                            catch {tail.Dispose();throw;}
                            status($"Frame {item.number}, offset {match.Delta}px, confidence {match.Confidence:P0}");
                        }
                    }
                }
                finally {previous?.Dispose();previous=current;}
            }
        }
        finally {previous?.Dispose();strips.Writer.TryComplete();}
    }

    private async Task WriteAsync()
    {
        using var document = new PdfPageWriter(output, region.Width);
        await foreach (var strip in strips.Reader.ReadAllAsync(failed.Token))
        {
            using (strip) document.Append(strip);
            Volatile.Write(ref pagesWritten, document.PagesWritten);
        }
        status("Finalizing PDF...");
        document.Finish();
        Volatile.Write(ref pagesWritten, document.PagesWritten);
    }

    public async Task StopAsync()
    {
        captureStop.Cancel();
        try {if(work is not null) await work;}
        finally {captureStop.Dispose();failed.Dispose();}
    }
}