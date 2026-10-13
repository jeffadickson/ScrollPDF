using System.Drawing;
using System.Windows.Forms;
namespace ScrollPDF;

public sealed class MainForm : Form
{
    private readonly Button region = new() { Text = "Select capture region", Width = 175 };
    private readonly CheckBox rawOnly = new() { Text = "Save raw PNG frames only", Checked = true, AutoSize = true };
    private readonly CheckBox videoOnly = new() { Text = "Experimental video recording (30 FPS, lossless)", AutoSize = true };
    private VideoRecorder? videoRecorder;
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
    private static readonly Color BackgroundColor = Color.FromArgb(16, 23, 35);
    private static readonly Color SurfaceColor = Color.FromArgb(29, 40, 56);
    private static readonly Color TextColor = Color.FromArgb(234, 240, 248);
    private static readonly Color MutedColor = Color.FromArgb(157, 175, 194);
    private static readonly Color AccentColor = Color.FromArgb(64, 178, 225);

    private static Panel Section(string title, Control content, int height)
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = height, BackColor = SurfaceColor, Padding = new Padding(16, 10, 16, 10) };
        var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 27,
            ForeColor = MutedColor, Font = new Font("Segoe UI Semibold", 10f) };
        panel.Controls.Add(content);
        panel.Controls.Add(heading);
        return panel;
    }

    public MainForm()
    {
        Text = "ScrollPDF  |  Capture Studio";
        Width = 790; Height = 530; MinimumSize = new Size(710, 490);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BackgroundColor; ForeColor = TextColor;
        Font = new Font("Segoe UI", 10f);

        var heading = new Label { Text = "ScrollPDF", Font = new Font("Segoe UI Semibold", 23f),
            Dock = DockStyle.Top, Height = 55, ForeColor = TextColor };
        var subtitle = new Label { Text = "Lossless scrolling capture  ·  Adaptive PNG recording",
            Dock = DockStyle.Top, Height = 30, ForeColor = MutedColor };

        foreach (var button in new[] { region, start, stop })
        {
            button.Height = 36; button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = SurfaceColor; button.ForeColor = TextColor;
            button.Margin = new Padding(0, 0, 10, 0);
        }
        start.BackColor = Color.FromArgb(25, 113, 151);
        stop.BackColor = Color.FromArgb(125, 70, 65);

        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, WrapContents = false };
        controls.Controls.AddRange([region, start, stop]);

        rawOnly.ForeColor = TextColor; rawOnly.Dock = DockStyle.Top;
        rawOnly.Height = 29;
        status.Dock = DockStyle.Fill; status.ForeColor = TextColor;
        status.Font = new Font("Segoe UI", 10f);
        status.AutoEllipsis = true;
        counters.Dock = DockStyle.Fill;
        counters.ForeColor = TextColor;
        counters.Font = new Font("Consolas", 11f);
        counters.TextAlign = ContentAlignment.MiddleLeft;

        queueLabel.ForeColor = MutedColor;
        queueLabel.Height = 27; queueLabel.Dock = DockStyle.Top;
        queueBar.Dock = DockStyle.Top; queueBar.Height = 25;
        queueBar.Style = ProgressBarStyle.Continuous;

        var queueBody = new Panel { Dock = DockStyle.Fill };
        queueBody.Controls.Add(queueBar);
        queueBody.Controls.Add(queueLabel);
        activity.Dock = DockStyle.Bottom; activity.Height = 10;

        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 15, 20, 15) };
        Controls.Add(content);
        var stack = new Panel { Dock = DockStyle.Top, Height = 450 };
        content.Controls.Add(stack);

        // DockStyle.Top controls are stacked in reverse insertion order.
        stack.Controls.Add(Section("STATUS", status, 68));
        stack.Controls.Add(Section("PNG WRITER QUEUE", queueBody, 94));
        stack.Controls.Add(Section("CAPTURE METRICS", counters, 109));
        stack.Controls.Add(videoOnly);
        stack.Controls.Add(rawOnly);
        stack.Controls.Add(controls);
        stack.Controls.Add(subtitle);
        stack.Controls.Add(heading);
        stack.Controls.Add(activity);

        videoOnly.CheckedChanged += (_,_) => { if (videoOnly.Checked) rawOnly.Checked = false; };
        rawOnly.CheckedChanged += (_,_) => { if (rawOnly.Checked) videoOnly.Checked = false; };
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
            if (pipeline is not null || recorder is not null || videoRecorder is not null)
            {
                e.Cancel = true;
                status.Text = "Stop and save before closing.";
            }
        };
    }

    private void RefreshCounters()
    {
        var p = pipeline;
        if (videoRecorder is { } video)
        {
            counters.Text = $"Lossless RGB video     30 FPS target\\nElapsed: {video.Seconds:F1} s   Recording: {video.Running}";
            queueLabel.Text = "Video encoder: FFmpeg (independent process)";
            queueBar.Value = 0;
            if (!video.Running && !finishing) status.Text = "Video process ended; stop and inspect the MKV.";
            return;
        }
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
                $"Capture: {r.ActualFps:F1} FPS (target {RawFrameRecorder.TargetFps})   Worst gap: {r.MaximumGapMilliseconds} ms\n" +
                $"PNG: {r.CompressionMode}   Optimal: {r.OptimalFrames:N0}   Fast: {r.FastestFrames:N0}   Raw: {r.UncompressedFrames:N0}";
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
        if (videoOnly.Checked)
        {
            using var chooser = new SaveFileDialog { Filter = "Matroska video (*.mkv)|*.mkv",
                DefaultExt = "mkv", FileName = $"ScrollPDF-{DateTime.Now:yyyyMMdd-HHmmss}.mkv" };
            if (chooser.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                videoRecorder = new VideoRecorder(bounds.Value,chooser.FileName);
                await videoRecorder.StartAsync();
                start.Enabled = region.Enabled = rawOnly.Enabled = videoOnly.Enabled = false;
                stop.Enabled = true; activity.Visible = true;
                status.Text = "Recording lossless video; scroll through the document.";
            }
            catch(Exception ex) { videoRecorder=null; MessageBox.Show(this,ex.Message,"Video capture error"); }
            return;
        }
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
        if (videoRecorder is { } video)
        {
            stop.Enabled = false; finishing = true;
            try
            {
                status.Text = "Finalizing lossless MKV...";
                await video.StopAsync();
                status.Text = "Saved lossless capture: " + video.Output;
            }
            catch(Exception ex)
            {
                status.Text = "Video capture failed: " + ex.Message;
                MessageBox.Show(this,ex.Message,"Video capture error");
            }
            finally
            {
                videoRecorder = null; finishing = false; activity.Visible = false;
                start.Enabled = region.Enabled = rawOnly.Enabled = videoOnly.Enabled = true;
            }
            return;
        }
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
                start.Enabled = region.Enabled = rawOnly.Enabled = videoOnly.Enabled = true;
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