using System.Drawing;
using System.Windows.Forms;
namespace ScrollPDF;
public sealed class RegionPicker : Form
{
    private Point origin, latest; private bool dragging;
    public Rectangle SelectedRegion { get; private set; }
    public RegionPicker()
    {
        var desktop = SystemInformation.VirtualScreen;
        Bounds = desktop; StartPosition = FormStartPosition.Manual; FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
        BackColor = Color.Black; Opacity = .30; Cursor = Cursors.Cross;
        MouseDown += (_,e) => { if(e.Button!=MouseButtons.Left) return; origin=latest=e.Location; dragging=true; Invalidate(); };
        MouseMove += (_,e) => { if(dragging){ latest=e.Location; Invalidate(); } };
        MouseUp += (_,e) => {
            if(!dragging)return; latest=e.Location; dragging=false;
            var r=Rectangle.FromLTRB(Math.Min(origin.X,latest.X),Math.Min(origin.Y,latest.Y),Math.Max(origin.X,latest.X),Math.Max(origin.Y,latest.Y));
            if(r.Width<80||r.Height<80)return;
            SelectedRegion=new Rectangle(r.X+Left,r.Y+Top,r.Width,r.Height); DialogResult=DialogResult.OK;Close();
        };
        KeyDown += (_,e)=> { if(e.KeyCode==Keys.Escape) {DialogResult=DialogResult.Cancel;Close();} };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if(!dragging)return;
        var r=Rectangle.FromLTRB(Math.Min(origin.X,latest.X),Math.Min(origin.Y,latest.Y),Math.Max(origin.X,latest.X),Math.Max(origin.Y,latest.Y));
        using var pen=new Pen(Color.Lime,3);e.Graphics.DrawRectangle(pen,r);
    }
}