using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VictusSetup;

internal static class SelfTest
{
    internal static int Run()
    {
        var directory=Path.Combine(Path.GetTempPath(),"VictusSetup-fixture-"+Guid.NewGuid().ToString("N"));
        try
        {
            VictusFanControl.Recovery.ProductRecoveryClientSelfTest.Run((ok, message) => { if (!ok) throw new InvalidOperationException(message); });
            static MemoryStream Build(string? extra=null,bool tamper=false,bool duplicate=false,string version="1.1.2")
            {
                var bytes=Encoding.UTF8.GetBytes("fixture only");
                var files=new[]{"Install-VictusFanControl.ps1","Start-ProductGui.ps1","app/VictusFanControl.App.exe"};
                var memory=new MemoryStream();
                using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
                {
                    foreach(var name in files){using var writer=zip.CreateEntry(name).Open();writer.Write(bytes);}
                    var manifest=JsonSerializer.Serialize(new{schemaVersion=1,kind="VictusFanControl.ProductGuiRelease",version,appDirectory="app",sourceHead=new string('a',40),finalReleaseReady=true,
                        files=files.Select(path=>new{path,size=bytes.Length,sha256=tamper?new string('0',64):Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()})});
                    using(var writer=new StreamWriter(zip.CreateEntry("PRODUCT-GUI-MANIFEST.json").Open()))writer.Write(manifest);
                    if(extra is not null){using var writer=zip.CreateEntry(extra).Open();writer.Write(bytes);}
                    if(duplicate){using var writer=zip.CreateEntry("START-PRODUCTGUI.PS1").Open();writer.Write(bytes);}
                }
                memory.Position=0;return memory;
            }
            using(var valid=Build())Package.Extract(valid,directory);
            if(Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Length!=4)throw new Exception("Fixture extraction incomplete.");
            var cases=new List<MemoryStream>{Build(tamper:true),Build(duplicate:true),Build(version:"1.0.0")};
            cases.AddRange(new[]{"../escape","/absolute","C:/escape","app\\escape","app/file:stream","app/CON.txt","app/dot.","app/space ","app//double","unlisted"}.Select(x=>Build(extra:x)));
            foreach(var invalid in cases)
            {
                using(invalid)
                {
                    bool refused=false;try{Package.Extract(invalid,directory+"-rejected");}catch(InvalidDataException){refused=true;}
                    if(!refused||Directory.Exists(directory+"-rejected"))throw new Exception("Unsafe package wrote to disk.");
                }
            }
            Console.WriteLine("Product installer self-test: PASS (14 package cases; traversal, ADS, device names, duplicates, hashes, extras and version; no hardware or startup registration).");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
