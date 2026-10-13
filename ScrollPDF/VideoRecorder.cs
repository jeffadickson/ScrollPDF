using System.Diagnostics;
using System.Drawing;
using System.Globalization;

namespace ScrollPDF;

/// <summary>
/// Experimental lossless screen-video recording backend.
/// Requires ffmpeg.exe in PATH. Video frames are RGB/H.264 lossless (libx264rgb).
/// No network traffic or PNG writer is involved.
/// </summary>
public sealed class VideoRecorder
{
    private readonly Rectangle region;
    private readonly string output;
    private Process? process;
    private Task? stderrReader;
    private readonly Stopwatch elapsed = new();
    private string? error;
    public bool Running => process is { HasExited: false };
    public string Output => output;
    public double Seconds => elapsed.Elapsed.TotalSeconds;
    public string? Error => error;
    public VideoRecorder(Rectangle region,string output)
    {
        this.region = region;
        this.output = output;
    }

    public static string CaptureArguments(Rectangle r,string output,int fps=30)
    {
        if(r.Width<64||r.Height<64||fps<1||fps>60)
            throw new ArgumentOutOfRangeException(nameof(r));
        // gdigrab's x/y refer to the virtual desktop origin; quote output
        // with ArgumentList rather than interpolating user-controlled paths.
        return $"-f gdigrab -framerate {fps} -offset_x {r.X} -offset_y {r.Y} -video_size {r.Width}x{r.Height} -i desktop " +
               "-an -c:v libx264rgb -preset ultrafast -qp 0 -pix_fmt bgr0 -f matroska";
    }

    public async Task StartAsync()
    {
        if(process!=null) throw new InvalidOperationException("Already started");
        if(File.Exists(output)) throw new IOException("Output file already exists");
        var info=new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute=false,
            RedirectStandardInput=true,
            RedirectStandardError=true,
            CreateNoWindow=true
        };
        var args=CaptureArguments(region,output).Split(' ',StringSplitOptions.RemoveEmptyEntries);
        foreach(var arg in args) info.ArgumentList.Add(arg);
        info.ArgumentList.Add(output);
        process=Process.Start(info) ?? throw new InvalidOperationException("Could not start FFmpeg");
        stderrReader=Task.Run(async () =>
        {
            string history = "";
            while(await process.StandardError.ReadLineAsync() is { } line)
            {
                if(line.Contains("error",StringComparison.OrdinalIgnoreCase))
                    history=line;
            }
            error=history.Length>0 ? history : null;
        });
        elapsed.Start();
        await Task.Delay(350);
        if(process.HasExited)
            throw new InvalidOperationException("FFmpeg exited at startup. Check that FFmpeg is installed and libx264rgb/gdigrab are supported. "+error);
    }

    public async Task StopAsync()
    {
        if(process==null)return;
        if(!process.HasExited)
        {
            await process.StandardInput.WriteLineAsync("q");
            await process.StandardInput.FlushAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch(OperationCanceledException)
            {
                process.Kill(entireProcessTree:true);
                await process.WaitForExitAsync();
                throw new IOException("Video writer timed out; MKV may be incomplete");
            }
        }
        if(stderrReader!=null)await stderrReader;
        elapsed.Stop();
        int code=process.ExitCode;
        process.Dispose();
        process=null;
        if(code!=0)throw new IOException("FFmpeg recording failed (exit "+code+"): "+error);
        if(!File.Exists(output)||new FileInfo(output).Length==0)
            throw new IOException("No video was written");
    }
}
