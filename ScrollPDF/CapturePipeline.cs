using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Channels;
namespace ScrollPDF;
/// <summary>Independent capture, analysis and PDF stages; bounded channels impose backpressure.</summary>
public sealed class CapturePipeline
{
    private readonly Rectangle region; private readonly string path; private readonly Action<string> progress;
    private readonly CancellationTokenSource captureStop=new();
    private readonly Channel<Bitmap> frames=Channel.CreateBounded<Bitmap>(new BoundedChannelOptions(4) {SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly Channel<Bitmap> strips=Channel.CreateBounded<Bitmap>(new BoundedChannelOptions(12) {SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
    private Task? producer, analyzer, writer;
    public CapturePipeline(Rectangle region,string path,Action<string> progress){this.region=region;this.path=path;this.progress=progress;}
    public Task StartAsync()
    {
        writer=Task.Run(WriteAsync);
        analyzer=Task.Run(AnalyzeAsync);
        producer=Task.Run(CaptureAsync);
        return Task.CompletedTask;
    }
    private async Task CaptureAsync()
    {
        try
        {
            while(!captureStop.IsCancellationRequested)
            {
                var bitmap=new Bitmap(region.Width,region.Height,PixelFormat.Format24bppRgb);
                try
                {
                    using(var graphics=Graphics.FromImage(bitmap)) graphics.CopyFromScreen(region.Location,Point.Empty,region.Size);
                    await frames.Writer.WriteAsync(bitmap,captureStop.Token);
                }
                catch { bitmap.Dispose();throw; }
                await Task.Delay(175,captureStop.Token);
            }
        }
        catch(OperationCanceledException) when(captureStop.IsCancellationRequested) { }
        finally { frames.Writer.TryComplete(); }
    }
    private async Task AnalyzeAsync()
    {
        Bitmap? previous=null;
        try
        {
            await foreach(var current in frames.Reader.ReadAllAsync())
            {
                try
                {
                    if(previous is null) await strips.Writer.WriteAsync(Geometry.Crop(current,new Rectangle(0,0,current.Width,current.Height)));
                    else
                    {
                        int shift=Geometry.FindScrollDelta(previous,current,(int)(current.Height*.72));
                        if(shift>0)
                        {
                            var tail=Geometry.Crop(current,new Rectangle(0,current.Height-shift,current.Width,shift));
                            await strips.Writer.WriteAsync(tail);
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
        using var document=new PdfPageWriter(path,region.Width);
        await foreach(var strip in strips.Reader.ReadAllAsync())
        {
            using(strip) document.Append(strip);
        }
        document.Finish();
    }
    public async Task StopAsync()
    {
        captureStop.Cancel();
        try { if(producer!=null)await producer; if(analyzer!=null)await analyzer; if(writer!=null)await writer; }
        finally {captureStop.Dispose();}
    }
}