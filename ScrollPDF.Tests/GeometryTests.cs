using System.Drawing;
using ScrollPDF;
using Xunit;
public class GeometryTests
{
    [Fact]
    public void IpcDocumentBoundary_is_detected()
    {
        using var bmp=new Bitmap(Path.Combine(AppContext.BaseDirectory,"Fixtures","ipc-page-break.png"));
        var result=Geometry.FindPageBreak(bmp);
        Assert.True(result.HasValue,"Expected gray viewer separator between white document pages");
        Assert.InRange(result.Value.start,30,75);
        Assert.InRange(result.Value.end,42,90);
    }
    [Fact]
    public void PlainWhitePage_has_no_boundary()
    {
        using var bmp=new Bitmap(320,240);
        using(var g=Graphics.FromImage(bmp)) g.Clear(Color.White);
        Assert.Null(Geometry.FindPageBreak(bmp));
    }
    [Fact]
    public void ScrollShift_is_measured()
    {
        using var large=new Bitmap(320,800);
        using(var g=Graphics.FromImage(large))
        {
            g.Clear(Color.White);
            for(int i=0;i<65;i++) {using var brush=new SolidBrush(Color.FromArgb((i*37)%220,(i*19)%205,(i*53)%240));g.FillRectangle(brush,0,i*12,320,4);}
        }
        using var prev=Geometry.Crop(large,new Rectangle(0,0,320,480));
        using var curr=Geometry.Crop(large,new Rectangle(0,70,320,480));
        Assert.InRange(Geometry.FindScrollDelta(prev,curr,200),68,72);
    }
    [Fact]
    public void PdfWriter_outputs_file()
    {
        string file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".pdf");
        try
        {
            using(var bitmap=new Bitmap(320,300))
            {
                using(var g=Graphics.FromImage(bitmap))g.Clear(Color.White);
                using(var writer=new PdfPageWriter(file,320,200)){writer.Append(bitmap);writer.Finish();}
            }
            Assert.True(new FileInfo(file).Length>100);
        }
        finally {if(File.Exists(file))File.Delete(file);}
    }
}