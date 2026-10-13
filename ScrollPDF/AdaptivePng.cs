using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;

namespace ScrollPDF;

/// <summary>Lossless RGB PNG encoder with selectable zlib effort; no PNG quantization.</summary>
public static class AdaptivePng
{
    public static CompressionLevel LevelForOccupancy(int queued, int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        double load = Math.Clamp((double)queued / capacity, 0, 1);
        return load >= .70 ? CompressionLevel.NoCompression
             : load >= .30 ? CompressionLevel.Fastest : CompressionLevel.Optimal;
    }

    public static void Save(Bitmap bitmap, string path, CompressionLevel level)
    {
        using var stream = File.Create(path);
        Write(bitmap, stream, level);
    }

    public static void Write(Bitmap bitmap, Stream stream, CompressionLevel level)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(stream);
        if (bitmap.PixelFormat != PixelFormat.Format24bppRgb)
            throw new ArgumentException("Capture must be 24-bit RGB",nameof(bitmap));
        int width=bitmap.Width, height=bitmap.Height;
        stream.Write(new byte[] {137,80,78,71,13,10,26,10});
        byte[] header=new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0,4),(uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4,4),(uint)height);
        header[8]=8; header[9]=2; // 8-bit truecolor RGB
        WriteChunk(stream,"IHDR",header);
        using var compressed=new MemoryStream();
        using(var zlib=new ZLibStream(compressed,level,leaveOpen:true))
        {
            int count=checked(width*3);
            var row=new byte[count+1];
            var previous=new byte[count];
            // GDI+ stores BGR rows; PNG needs RGB. The Up filter helps
            // compression of screenshots with repeated text and white areas.
            var region=new Rectangle(0,0,width,height);
            var locked=bitmap.LockBits(region,ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
            try
            {
                var scan=new byte[count];
                for(int y=0;y<height;y++)
                {
                    // Supports both positive and negative stride.
                    var pointer=IntPtr.Add(locked.Scan0,y*locked.Stride);
                    System.Runtime.InteropServices.Marshal.Copy(pointer,scan,0,count);
                    row[0]=2; // PNG Up predictor, applied to RGB bytes
                    for(int x=0;x<width;x++)
                    {
                        int i=x*3;
                        byte r=scan[i+2], g=scan[i+1], b=scan[i];
                        row[i+1]=unchecked((byte)(r-previous[i]));
                        row[i+2]=unchecked((byte)(g-previous[i+1]));
                        row[i+3]=unchecked((byte)(b-previous[i+2]));
                        previous[i]=r; previous[i+1]=g; previous[i+2]=b;
                    }
                    zlib.Write(row);
                }
            }
            finally {bitmap.UnlockBits(locked);}
        }
        WriteChunk(stream,"IDAT",compressed.ToArray());
        WriteChunk(stream,"IEND",Array.Empty<byte>());
    }

    private static void WriteChunk(Stream output,string type,byte[] data)
    {
        byte[] name=System.Text.Encoding.ASCII.GetBytes(type);
        Span<byte> word=stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word,(uint)data.Length);
        output.Write(word); output.Write(name); output.Write(data);
        uint crc=0xffffffff;
        foreach(byte b in name) crc=Update(crc,b);
        foreach(byte b in data) crc=Update(crc,b);
        BinaryPrimitives.WriteUInt32BigEndian(word,~crc);
        output.Write(word);
    }
    private static uint Update(uint crc,byte value)
    {
        crc ^= value;
        for(int k=0;k<8;k++) crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0u);
        return crc;
    }
}
