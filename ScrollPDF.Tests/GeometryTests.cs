using System.Drawing;
using ScrollPDF;
using Xunit;
public class GeometryTests
{
    [Fact]
    public void RawFrameZipPreservesFullSequenceAndVerifiesBytes()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scrollpdf-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string zipPath = folder + ".zip";
        try
        {
            for(int i=0;i<4;i++)
            {
                using var frame = new Bitmap(160,120);
                using(var graphics = Graphics.FromImage(frame))
                {
                    graphics.Clear(Color.White);
                    graphics.FillRectangle(Brushes.Black,10,10+i*20,110,4);
                }
                frame.Save(Path.Combine(folder,$"frame-{i:D7}.png"));
            }
            string result = FrameArchive.PackageAsync(folder).GetAwaiter().GetResult();
            Assert.Equal(zipPath,result);
            using var archive = System.IO.Compression.ZipFile.OpenRead(result);
            Assert.Equal(4,archive.Entries.Count);
            Assert.Equal(new[] {"frame-0000000.png","frame-0000001.png",
                "frame-0000002.png","frame-0000003.png"},
                archive.Entries.Select(e=>e.Name).OrderBy(n=>n).ToArray());
            Assert.Equal(4,Directory.GetFiles(folder,"*.png").Length);
        }
        finally
        {
            if(File.Exists(zipPath)) File.Delete(zipPath);
            if(Directory.Exists(folder)) Directory.Delete(folder,true);
        }
    }


    [Fact]
    public void PageBreakAcrossTwoStripsProducesTwoPdfPages()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            using var image = new Bitmap(320, 260);
            using (var g = Graphics.FromImage(image))
            {
                g.Clear(Color.White);
                using var gray = new SolidBrush(Color.FromArgb(195,195,195));
                g.FillRectangle(gray,0,123,320,10);
                g.FillRectangle(Brushes.Black,50,60,150,5);
                g.FillRectangle(Brushes.Black,50,180,150,5);
            }
            using var upper = Geometry.Crop(image,new Rectangle(0,0,320,127));
            using var lower = Geometry.Crop(image,new Rectangle(0,127,320,133));
            using (var writer = new PdfPageWriter(file,320,800))
            {
                writer.Append(upper);
                Assert.Equal(0,writer.PagesWritten);
                writer.Append(lower);
                Assert.Equal(1,writer.PagesWritten);
                writer.Finish();
                Assert.Equal(2,writer.PagesWritten);
            }
            Assert.True(new FileInfo(file).Length > 100);
        }
        finally { if(File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public void RepeatedChartGridCannotProduceConfidentWrongOffset()
    {
        using var canvas=new Bitmap(480,1200);
        using(var g=Graphics.FromImage(canvas))
        {
            g.Clear(Color.White);
            using var pen=new Pen(Color.Gray);
            for(int y=0;y<1200;y+=16) g.DrawLine(pen,40,y,440,y);
            for(int x=40;x<440;x+=18) g.DrawLine(pen,x,0,x,1200);
            for(int y=0;y<1200;y+=137) g.FillRectangle(Brushes.Black,60+y%87,y+7,23,3);
        }
        using var first=Geometry.Crop(canvas,new Rectangle(0,20,480,600));
        using var second=Geometry.Crop(canvas,new Rectangle(0,93,480,600));
        var m=Geometry.RegisterScroll(first,second,300);
        Assert.True(m.Status!=Geometry.MatchStatus.Matched || Math.Abs(m.Delta-73)<=3,
           $"Incorrect confident displacement: {m.Delta}px");
    }
    [Fact]
    public void BlankPagesAreNeverConfidentlyMatched()
    {
        using var first=new Bitmap(480,600);
        using var second=new Bitmap(480,600);
        using(var g=Graphics.FromImage(first))g.Clear(Color.White);
        using(var g=Graphics.FromImage(second))g.Clear(Color.White);
        Assert.NotEqual(Geometry.MatchStatus.Matched,Geometry.RegisterScroll(first,second,300).Status);
    }

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