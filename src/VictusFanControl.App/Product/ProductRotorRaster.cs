namespace VictusFanControl.App;

// Inverse-mapped bilinear rotation of premultiplied BGRA pixels. Reuses buffers;
// GDI+ only copies the axis-aligned result instead of resampling a rotated image.
internal static class ProductRotorRaster
{
    internal static void Rotate(int[] source,int size,double degrees,int[] output)
    {
        if(size<1||source.Length!=size*size||output.Length!=source.Length||ReferenceEquals(source,output))
            throw new ArgumentException("Invalid rotor raster buffers.");
        var angle=degrees*Math.PI/180;var cos=(float)Math.Cos(angle);var sin=(float)Math.Sin(angle);
        var centre=(size-1)*.5f;
        int target=0;
        for(int y=0;y<size;y++)
        {
            var sx=centre-centre*cos+(y-centre)*sin;
            var sy=centre+centre*sin+(y-centre)*cos;
            for(int x=0;x<size;x++,target++,sx+=cos,sy-=sin)
            {
                if(sx<0||sy<0||sx>=size||sy>=size){output[target]=0;continue;}
                int ix=(int)sx,iy=(int)sy;
                int nx=Math.Min(ix+1,size-1),ny=Math.Min(iy+1,size-1);
                uint a=(uint)source[iy*size+ix],b=(uint)source[iy*size+nx],c=(uint)source[ny*size+ix],d=(uint)source[ny*size+nx];
                if((a|b|c|d)==0){output[target]=0;continue;}
                uint fx=(uint)((sx-ix)*256),fy=(uint)((sy-iy)*256);
                uint wa=((256-fx)*(256-fy))>>8,wb=(fx*(256-fy))>>8,wc=((256-fx)*fy)>>8,wd=256-wa-wb-wc;
                uint rb=(((a&0x00ff00ff)*wa+(b&0x00ff00ff)*wb+(c&0x00ff00ff)*wc+(d&0x00ff00ff)*wd)>>8)&0x00ff00ff;
                uint ga=((((a>>8)&0x00ff00ff)*wa+((b>>8)&0x00ff00ff)*wb+((c>>8)&0x00ff00ff)*wc+((d>>8)&0x00ff00ff)*wd)>>8)&0x00ff00ff;
                output[target]=(int)(rb|(ga<<8));
            }
        }
    }
}
