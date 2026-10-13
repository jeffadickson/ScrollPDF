using System.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
namespace ScrollPDF;
/// <summary>Single-owner PDF writer. Image conversion and PDF mutation remain serialized.</summary>
public sealed class PdfPageWriter : IDisposable
{
    private readonly PdfDocument pdf=new();
    private readonly string target; private readonly int width;
    private readonly int targetPixels;
    private readonly List<Bitmap> chunks=new();
    private int bufferedHeight;
    private bool finished;
    public PdfPageWriter(string path,int width,int pageHeight=0)
    {
        target=path;this.width=width;
        targetPixels=pageHeight>0?pageHeight:(int)Math.Round(width*11.0/8.5);
    }
    public void Append(Bitmap input)
    {
        if(finished) throw new InvalidOperationException("Already finished");
        var boundary=Geometry.FindPageBreak(input);
        if(boundary is { } b)
        {
            if(b.start>0) AppendPixels(input,0,b.start);
            FlushPage();
            if(b.end<input.Height) AppendPixels(input,b.end,input.Height-b.end);
        }
        else AppendPixels(input,0,input.Height);
    }
    private void AppendPixels(Bitmap source,int y,int height)
    {
        while(height>0)
        {
            var space=targetPixels-bufferedHeight;
            var count=Math.Min(space,height);
            if(count==0) {FlushPage();continue;}
            chunks.Add(Geometry.Crop(source,new Rectangle(0,y,width,count)));
            bufferedHeight+=count;y+=count;height-=count;
            if(bufferedHeight>=targetPixels)FlushPage();
        }
    }
    private void FlushPage()
    {
        if(bufferedHeight==0)return;
        using var composed=new Bitmap(width,bufferedHeight);
        using(var g=Graphics.FromImage(composed))
        {
            int y=0;foreach(var part in chunks){g.DrawImageUnscaled(part,0,y);y+=part.Height;part.Dispose();}
        }
        chunks.Clear();bufferedHeight=0;
        using var buffer=new MemoryStream();composed.Save(buffer,System.Drawing.Imaging.ImageFormat.Png);buffer.Position=0;
        var page=pdf.AddPage();page.Size=PdfSharp.PageSize.Letter;
        using var gfx=XGraphics.FromPdfPage(page);
        using var image=XImage.FromStream(buffer);
        const double margin=18;
        double scale=Math.Min((page.Width.Point-2*margin)/width,(page.Height.Point-2*margin)/composed.Height);
        gfx.DrawImage(image,margin,margin,composed.Width*scale,composed.Height*scale);
    }
    public void Finish()
    {
        if(finished)return;FlushPage();
        if(pdf.PageCount==0) throw new InvalidOperationException("No content captured");
        pdf.Save(target);finished=true;
    }
    public void Dispose(){foreach(var c in chunks)c.Dispose();pdf.Dispose();}
}