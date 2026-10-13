using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VictusFanControl.App;

// Small opaque child window: WinForms buffers only the rotor, not a 4K canvas.
internal sealed class ProductFanSurface : System.Windows.Forms.Control
{
    private Bitmap? _frame;
    private int[] _background=[],_overlay=[],_pixels=[];
    private readonly ProductFanRotor _rotor;
    private float _scale;
    private Color _color;
    internal double Phase {get;set;}
    internal ProductFanSurface(ProductFanRotor rotor)
    {
        _rotor=rotor;TabStop=false;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Opaque,true);
    }
    internal void SetLayers(Bitmap background,Bitmap overlay,Rectangle source,float scale,Color color)
    {
        _scale=scale;_color=color;
        _frame?.Dispose();_frame=new(Width,Height,PixelFormat.Format32bppPArgb);
        _background=Read(background,source);_overlay=Read(overlay,source);_pixels=new int[Width*Height];
        Invalidate();
    }
    private static int[] Read(Bitmap image,Rectangle rect)
    {
        using var crop=image.Clone(rect,PixelFormat.Format32bppPArgb);
        var result=new int[rect.Width*rect.Height];
        var bits=crop.LockBits(new(Point.Empty,crop.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
        try{Marshal.Copy(bits.Scan0,result,0,result.Length);}finally{crop.UnlockBits(bits);}return result;
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        RenderFrame(e.Graphics);
    }
    internal void RenderFrame(Graphics graphics)
    {
        if(_frame is null)return;
        var size=Math.Clamp((int)Math.Ceiling(176*_scale),16,1024);
        var rotor=_rotor.Rasterize(size,Phase,_color);
        var ox=(Width-size)/2;var oy=(Height-size)/2;
        for(int y=0,k=0;y<Height;y++)for(int x=0;x<Width;x++,k++)
        {
            var pixel=_background[k];var sx=x-ox;var sy=y-oy;
            if(sx>=0&&sy>=0&&sx<size&&sy<size)pixel=Over(pixel,rotor[sy*size+sx]);
            _pixels[k]=Over(pixel,_overlay[k]);
        }
        var bits=_frame.LockBits(new(Point.Empty,_frame.Size),ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);
        try{Marshal.Copy(_pixels,0,bits.Scan0,_pixels.Length);}finally{_frame.UnlockBits(bits);}
        graphics.DrawImageUnscaled(_frame,0,0);
    }
    private static int Over(int background,int foreground)
    {
        uint fg=(uint)foreground,bg=(uint)background;var a=fg>>24;
        if(a==0)return background;if(a==255)return foreground;
        var inverse=255-a;
        var b=(fg&255)+((bg&255)*inverse+127)/255;
        var g=((fg>>8)&255)+(((bg>>8)&255)*inverse+127)/255;
        var r=((fg>>16)&255)+(((bg>>16)&255)*inverse+127)/255;
        return (int)(0xff000000|r<<16|g<<8|b);
    }
    protected override void Dispose(bool disposing){if(disposing)_frame?.Dispose();base.Dispose(disposing);}
}
