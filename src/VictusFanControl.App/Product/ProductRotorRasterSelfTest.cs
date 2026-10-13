namespace VictusFanControl.App;

internal static class ProductRotorRasterSelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        var source=new[]{unchecked((int)0xff010101),unchecked((int)0xff020202),unchecked((int)0xff030303),unchecked((int)0xff040404)};
        var rotated=new int[4];ProductRotorRaster.Rotate(source,2,0,rotated);
        require(rotated.SequenceEqual(source),"Zero-angle raster altered pixels.");
        ProductRotorRaster.Rotate(source,2,90,rotated);
        require(rotated.SequenceEqual(new[]{source[2],source[0],source[3],source[1]}),"Quarter-turn rotor used the wrong pivot or direction.");
        var transparent=new int[16];var output=new int[16];ProductRotorRaster.Rotate(transparent,4,33,output);
        require(output.All(p=>p==0),"Transparent PNG pixels fabricated a background.");
        var premultiplied=Enumerable.Repeat(unchecked((int)0x80604020),256).ToArray();var result=new int[256];
        foreach(var phase in new[]{0d,.1,27.4,89.9,180,359.9})
        {
            ProductRotorRaster.Rotate(premultiplied,16,phase,result);
            require(result.All(p=>{uint v=(uint)p,a=v>>24;return (v&255)<=a&&((v>>8)&255)<=a&&((v>>16)&255)<=a;}),
                "Bilinear rotation violated premultiplied alpha.");
        }
        Console.WriteLine("PNG rotor raster: PASS (identity, clockwise quarter-turn/pivot, transparency and premultiplied-alpha interpolation).");
    }
}
