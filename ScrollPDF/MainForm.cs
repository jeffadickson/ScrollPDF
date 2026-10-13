using System.Drawing;
using System.Windows.Forms;
namespace ScrollPDF;
public sealed class MainForm : Form
{
    private readonly Button region = new() { Text = "Select capture region", Width = 175 };
    private readonly Button start = new() { Text = "Start", Width = 90 };
    private readonly Button stop = new() { Text = "Stop", Width = 90, Enabled = false };
    private readonly Label status = new() { Text = "Select a region, then start capturing. Scroll slowly downward.", AutoSize = true };
    private Rectangle? bounds;
    private CapturePipeline? pipeline;
    public MainForm()
    {
        Text = "ScrollPDF"; Width = 610; Height = 180; MinimumSize = new Size(580,180);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8) };
        bar.Controls.AddRange([region,start,stop]); Controls.Add(bar);
        status.Dock = DockStyle.Fill; status.Padding = new Padding(12); Controls.Add(status);
        region.Click += (_,_) => { using var picker = new RegionPicker(); if (picker.ShowDialog(this) == DialogResult.OK) { bounds = picker.SelectedRegion; status.Text = $"Region: {bounds}"; } };
        start.Click += async (_,_) => await BeginCapture();
        stop.Click += async (_,_) => await EndCapture();
        FormClosing += (_,e) => { if (pipeline is not null) { e.Cancel = true; status.Text = "Stop capture before closing."; } };
    }
    private async Task BeginCapture()
    {
        if (bounds is null) { MessageBox.Show("Select a screen region first."); return; }
        using var dialog = new SaveFileDialog { Filter="PDF document (*.pdf)|*.pdf", FileName="capture.pdf", DefaultExt="pdf" };
        if (dialog.ShowDialog(this)!=DialogResult.OK) return;
        try
        {
            pipeline = new CapturePipeline(bounds.Value,dialog.FileName,s => BeginInvoke(new Action(() => status.Text = s)));
            start.Enabled = false; stop.Enabled = true; region.Enabled = false;
            await pipeline.StartAsync();
        }
        catch (Exception ex) { pipeline = null; start.Enabled = region.Enabled = true; stop.Enabled = false; MessageBox.Show(ex.Message,"Capture error"); }
    }
    private async Task EndCapture()
    {
        if (pipeline is null) return;
        stop.Enabled = false;
        try { await pipeline.StopAsync(); status.Text = "PDF saved."; }
        catch (Exception ex) { status.Text = "Capture failed: " + ex.Message; }
        finally { pipeline = null; start.Enabled = region.Enabled = true; }
    }
}