using System.Drawing;
using System.Windows.Forms;
namespace ScrollPDF;

public sealed class MainForm : Form
{
    private readonly Button region = new() { Text = "Select capture region", Width = 175 };
    private readonly CheckBox rawOnly = new() { Text = "Save raw PNG frames only", Checked = true, AutoSize = true };
    private readonly Button start = new() { Text = "Start", Width = 90 };
    private readonly Button stop = new() { Text = "Stop and save", Width = 110, Enabled = false };
    private readonly Label status = new() { Text = "Select a region and start scrolling.", AutoSize = false, Height = 28, Dock = DockStyle.Top };
    private readonly Label counters = new() { AutoSize = false, Height = 90, Dock = DockStyle.Top,
        Font = new Font("Consolas",10), Text = "Frames: 0   Analyzed: 0   Saved: 0" };
    private readonly ProgressBar activity = new() { Dock = DockStyle.Top, Height = 18, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly ProgressBar queueBar = new() { Dock = DockStyle.Top, Height = 23, Minimum = 0,
        Maximum = 100, Style = ProgressBarStyle.Continuous };
    private readonly Label queueLabel = new() { Dock = DockStyle.Top, Height = 22,
        Text = "PNG writer queue: 0 frames" };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 250 };
    private Rectangle? bounds;
    private CapturePipeline? pipeline;
    private RawFrameRecorder? recorder;
    private string? zipDestination;
    private bool finishing;
    private int zipPercent = -1;
    private bool handlingUnexpectedStop;
    public MainForm()
    {
        Text = "ScrollPDF – capture monitor";
        Width = 680; Height = 365; MinimumSize = new Size(610,330);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8) };
        bar.Controls.AddRange([region,start,stop]);
        var mode = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32 };
        mode.Controls.Add(rawOnly);
        Controls.Add(counters);
        Controls.Add(queueBar);
        Controls.Add(queueLabel);
        Controls.Add(mode);
        Controls.Add(activity);
        Controls.Add(status);
        Controls.Add(bar);
        refresh.Tick += (_,_) => RefreshCounters();
        refresh.Start();
        region.Click += (_,_) => {
            using var picker = new RegionPicker();
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                bounds = picker.SelectedRegion;
                status.Text = $"Selected region: {bounds}";
            }
        };
        start.Click += async (_,_) => await BeginCapture();
        stop.Click += async (_,_) => await EndCapture();
        FormClosing += (_,e) => {
            if (pipeline is not null || recorder is not null) { e.Cancel = true; status.Text = "Stop and save before closing."; }
        };
    }

    private void RefreshCounters()
    {
        var p = pipeline;
        if (zipPercent >= 0)
        {
            queueBar.Maximum = 100;
            queueBar.Value = Math.Clamp(zipPercent, 0, 100);
            queueLabel.Text = $"Verifying ZIP archive: {zipPercent}%";
            return;
        }
        if (recorder is { } r)
        {
            queueBar.Maximum = r.QueueCapacity;
            int queued = r.QueueSize;
            queueBar.Value = Math.Clamp(queued, queueBar.Minimum, queueBar.Maximum);
            queueLabel.Text = $"PNG writer queue: {queued} / {r.QueueCapacity} frames ({queued * 100 / r.QueueCapacity}%)";
            counters.Text = $"Captured: {r.Captured:N0}     PNG saved: {r.Saved:N0}     Waiting/in flight: {r.Captured-r.Saved:N0}\n" +
                $"Capture: {r.ActualFps:F1} FPS (target {RawFrameRecorder.TargetFps})   Worst gap: {r.MaximumGapMilliseconds} ms   PNG effort: {r.CompressionMode}";
            if (r.Finished && !finishing) status.Text = "Recording ended. Check image folder.";
            return;
        }
        if (p is null) return;
        queueBar.Value = 0;
        queueLabel.Text = "PNG-only writer queue: inactive during direct PDF capture";
        if (!finishing && !handlingUnexpectedStop && p.IsFinished)
        {
            handlingUnexpectedStop = true;
            _ = HandleUnexpectedStopAsync(p);
        }
        counters.Text =
            $"Captured: {p.CapturedFrames,7:N0}   Analyzed: {p.AnalyzedFrames,7:N0}   Archived: {p.ArchivedFrames,7:N0}\n" +
            $"PDF pages: {p.PagesWritten,5}   Last scroll: {p.LastDisplacement,5} px   Confidence: {p.LastConfidence,6:P0}\n" +
            $"Pending capture: {Math.Max(0, p.CapturedFrames - p.AnalyzedFrames),5:N0}   " +
            $"Pending archive: {Math.Max(0, p.CapturedFrames - p.ArchivedFrames),5:N0}";
        if (finishing) status.Text = "Stopping capture; draining queues and writing final PDF...";
    }

    private async Task BeginCapture()
    {
        if (bounds is null) { MessageBox.Show("Select a screen region first."); return; }
        if (rawOnly.Checked)
        {
            using var chooser = new SaveFileDialog { Filter="ZIP archive (*.zip)|*.zip", DefaultExt="zip", FileName=$"ScrollPDF-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
            if (chooser.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                zipDestination = chooser.FileName;
                recorder = new RawFrameRecorder(bounds.Value, Path.Combine(Path.GetTempPath(), "ScrollPDF-" + Guid.NewGuid().ToString("N")));
                await recorder.StartAsync();
                start.Enabled = region.Enabled = rawOnly.Enabled = false;
                stop.Enabled = true; activity.Visible = true;
                status.Text = "Recording raw frames; stitching disabled.";
            }
            catch(Exception ex) { recorder = null; MessageBox.Show(ex.Message,"Recorder error"); }
            return;
        }
        using var dialog = new SaveFileDialog {
            Filter = "PDF document (*.pdf)|*.pdf", FileName = "capture.pdf", DefaultExt = "pdf"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            pipeline = new CapturePipeline(bounds.Value, dialog.FileName, s => {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new Action(() => { if (!finishing) status.Text = s; }));
            });
            start.Enabled = false; stop.Enabled = true; region.Enabled = false;
            activity.Visible = true;
            status.Text = "Recording — scroll steadily down. Frames are archived for diagnostics.";
            await pipeline.StartAsync();
        }
        catch (Exception ex)
        {
            pipeline = null; start.Enabled = region.Enabled = true;
            stop.Enabled = false; activity.Visible = false;
            MessageBox.Show(ex.Message,"Capture error");
        }
    }

    private async Task HandleUnexpectedStopAsync(CapturePipeline stopped)
    {
        if (!ReferenceEquals(pipeline, stopped)) return;
        stop.Enabled = false;
        activity.Visible = false;
        try
        {
            await stopped.Completion;
            status.Text = "Capture ended unexpectedly. Check the saved PDF.";
        }
        catch (Exception ex)
        {
            var message = stopped.FailureMessage ?? ex.GetBaseException().Message;
            status.Text = "Capture interrupted: " + message;
            MessageBox.Show(this,
                "ScrollPDF stopped because a worker failed.\n\n" + message +
                "\n\nOriginal screenshots remain in:\n" + stopped.RawDirectory,
                "ScrollPDF capture interrupted", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (ReferenceEquals(pipeline, stopped)) pipeline = null;
            start.Enabled = region.Enabled = true;
            handlingUnexpectedStop = false;
        }
    }

    private async Task EndCapture()
    {
        if (recorder is { } r)
        {
            stop.Enabled = false; finishing = true;
            try
            {
                await r.StopAsync();
                status.Text = "Packing and verifying screenshots in background...";
                zipPercent = 0;
                var progress = new Progress<int>(p => zipPercent = p);
                string zip = await FrameArchive.PackageAsync(r.Folder, zipDestination!, progress);
                zipPercent = 100;
                status.Text = $"Saved {r.Saved:N0} screenshots to {zip}. Temporary staging folder removed.";
            }
            catch (Exception ex)
            {
                status.Text = "Archive failed: " + ex.Message + "  Recovery folder: " + r.Folder;
                MessageBox.Show(ex.Message, "Archive failed");
            }
            finally
            {
                recorder = null; zipDestination = null; finishing = false;
                zipPercent = -1;
                queueBar.Maximum = 100;
                queueBar.Value = 0;
                activity.Visible = false;
                start.Enabled = region.Enabled = rawOnly.Enabled = true;
            }
            return;
        }
        if (pipeline is null) return;
        stop.Enabled = false; finishing = true;
        try
        {
            await pipeline.StopAsync();
            RefreshCounters();
            status.Text = $"Saved PDF ({pipeline.PagesWritten} pages). Raw frames: {pipeline.RawDirectory}";
        }
        catch (Exception ex)
        {
            var message = pipeline?.FailureMessage ?? ex.GetBaseException().Message;
            status.Text = "Capture failed: " + message;
            MessageBox.Show(this, message + "\n\nRaw screenshots: " + pipeline?.RawDirectory,
                "ScrollPDF capture error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            finishing = false; pipeline = null;
            activity.Visible = false;
            start.Enabled = region.Enabled = true;
        }
    }
}