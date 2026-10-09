using System.Reflection;
using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

/// <summary>Runs real WinForms paint/drag/edit code; no MainForm or hardware construction.</summary>
internal static class AdaptiveCurveEditorSelfTest
{
    public static int Run()
    {
        var failures=0;
        void Check(string name,Action test)
        {
            try {test();Console.WriteLine("PASS: curve editor UI — "+name);}
            catch(Exception e){failures++;Console.WriteLine("FAIL: curve editor UI — "+name+": "+e);}
        }
        void Require(bool b){if(!b)throw new InvalidOperationException("Assertion failed.");}
        Check("point drag snaps and commits once",()=>
        {
            using var host=new Form{ClientSize=new Size(600,300)};
            using var chart=new AdaptiveCurveChart{Points=new[]{new AdaptiveFanCurvePoint(0,10),new AdaptiveFanCurvePoint(50,30),new AdaptiveFanCurvePoint(100,50)},MaximumInput=100};
            host.Controls.Add(chart);host.Show();Application.DoEvents();
            var commits=0;chart.DraftChanged+=_=>commits++;
            void Mouse(string method,int x,int y)=>typeof(AdaptiveCurveChart).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!
                .Invoke(chart,new object[]{new MouseEventArgs(MouseButtons.Left,1,x,y,0)});
            Mouse("OnMouseDown",315,137);Mouse("OnMouseMove",368,109);Mouse("OnMouseUp",368,109);
            Require(commits==1 && chart.Points[1]==new AdaptiveFanCurvePoint(60,35));
            using var bitmap=new Bitmap(chart.Width,chart.Height);chart.DrawToBitmap(bitmap,chart.ClientRectangle);
            var bluePixels=0;
            for(var x=0;x<bitmap.Width;x++)for(var y=0;y<bitmap.Height;y++)
            {var pixel=bitmap.GetPixel(x,y);if(pixel.B>pixel.R+40 && pixel.B>pixel.G)bluePixels++;}
            Require(bluePixels>50);host.Close();
        });
        var dir=Path.Combine(Path.GetTempPath(),"victus-editor-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            Check("numeric draft, undo/redo, explicit preview and resize/render",()=>
            {
                AdaptiveCurveProfile? applied=null;var count=0;
                using var editor=new AdaptiveCurveEditorForm(p=>{applied=p;count++;},AdaptiveCurveProfiles.Presets()[1],dir);
                editor.Show();Application.DoEvents();
                T Field<T>(string name)=>(T)typeof(AdaptiveCurveEditorForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(editor)!;
                void Invoke(string name)=>typeof(AdaptiveCurveEditorForm).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,null);
                var chart=Field<AdaptiveCurveChart>("_chart");
                Field<NumericUpDown>("_level").Value=12;Invoke("UpdatePoint");Require(chart.Points[0].Level==12 && count==0);
                Invoke("Undo");Require(chart.Points[0].Level==10 && count==0);
                Invoke("Redo");Require(chart.Points[0].Level==12 && count==0);
                Invoke("ApplyPreview");Require(count==1 && applied is not null && AdaptiveCurveProfiles.Validate(applied).CpuTemperatureCurve[0].Level==12);
                foreach(var size in new[]{new Size(1080,800),new Size(850,760)})
                {
                    editor.Size=size;editor.PerformLayout();Application.DoEvents();
                    Require(chart.Width>=420 && chart.Height>=250);
                    using var bitmap=new Bitmap(editor.Width,editor.Height);editor.DrawToBitmap(bitmap,new Rectangle(Point.Empty,editor.Size));
                    if(size.Width==1080)bitmap.Save("curve-editor-ui.png");
                }
                // Dispose rather than interactive Close: discard prompts are intentionally operator-owned.
            });
        }
        finally{if(Directory.Exists(dir))Directory.Delete(dir,true);}
        Console.WriteLine(failures==0 ? "Adaptive curve editor UI self-test: PASS (2 groups)" : "Adaptive curve editor UI self-test: FAIL");
        return failures==0 ? 0 : 37;
    }
}
