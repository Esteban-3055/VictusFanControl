using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VictusFanControl.App;

internal sealed class ProductFanRotor : IDisposable
{
    private readonly Bitmap _source;
    private Bitmap? _scaled,_rotated;
    private int[] _pixels=[],_output=[];
    private int _size;
    // Registration of the supplied 1254 px asset around its hub, not its canvas midpoint.
    private const float PivotX=626f/1254, PivotY=642.5f/1254;
    internal ProductFanRotor()
    {
        using var stream=typeof(ProductFanRotor).Assembly.GetManifestResourceStream("VictusFanControl.HomeFan.png")
            ??throw new InvalidDataException("Missing Home rotor resource.");
        using var image=Image.FromStream(stream);_source=new Bitmap(image);
    }
    internal void Draw(Graphics g,float x,float y,double phase,float scale,Color color)
    {
        var size=Math.Clamp((int)Math.Ceiling(176*scale),16,1024);
        Rasterize(size,phase,color);
        var frame=_rotated!.LockBits(new(0,0,size,size),ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);
        try{Marshal.Copy(_output,0,frame.Scan0,_output.Length);}finally{_rotated.UnlockBits(frame);}
        var saved=g.Save();g.TranslateTransform(x,y);
        g.InterpolationMode=InterpolationMode.Bilinear;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
        g.DrawImage(_rotated,new RectangleF(-88,-88,176,176));g.Restore(saved);
    }
    internal int[] Rasterize(int size,double phase,Color color)
    {
        if(size is <16 or >1024)throw new ArgumentOutOfRangeException(nameof(size));
        if(_scaled is null||_size!=size)
        {
            _scaled?.Dispose();_rotated?.Dispose();_scaled=new(size,size,PixelFormat.Format32bppPArgb);
            _rotated=new(size,size,PixelFormat.Format32bppPArgb);_pixels=new int[size*size];_output=new int[size*size];_size=size;
            using(var raster=Graphics.FromImage(_scaled))
            {raster.Clear(Color.Transparent);
            raster.InterpolationMode=InterpolationMode.HighQualityBicubic;raster.PixelOffsetMode=PixelOffsetMode.HighQuality;
            var destination=new Rectangle((int)Math.Round(size/2f-PivotX*size),(int)Math.Round(size/2f-PivotY*size),size,size);
            // Preserve the supplied RGBA artwork. Status colors belong to the
            // surrounding text/icons; they never recolor the rotor itself.
            raster.DrawImage(_source,destination,0,0,_source.Width,_source.Height,GraphicsUnit.Pixel);
            }
            var bits=_scaled.LockBits(new(0,0,size,size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
            try{Marshal.Copy(bits.Scan0,_pixels,0,_pixels.Length);}finally{_scaled.UnlockBits(bits);}
        }
        ProductRotorRaster.Rotate(_pixels,size,phase,_output);
        return _output;
    }
    public void Dispose(){_scaled?.Dispose();_rotated?.Dispose();_source.Dispose();}
}
