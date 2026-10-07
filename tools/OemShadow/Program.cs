using System.IO.Compression;
using System.Text.Json;
namespace VictusFanControl.OemShadow;
internal static class Program
{
    public static int Main(string[] args)
    {
        if(args.SequenceEqual(new[]{"--self-test"})) return SelfTests.Run();
        try
        {
            if(args.Length is 3 or 5 && args[0]=="--replay" && (args.Length==3||args[3]=="--parameters"))
            {
                var p=args.Length==5?JsonSerializer.Deserialize<Parameters>(File.ReadAllText(args[4]),ShadowSession.Json)??throw new InvalidDataException("Empty parameters"):new();
                using var session=new ShadowSession(args[2],p,"REPLAY");
                using var input=File.OpenRead(args[1]);
                using Stream stream=args[1].EndsWith(".gz",StringComparison.OrdinalIgnoreCase)?new GZipStream(input,CompressionMode.Decompress):input;
                using var reader=new StreamReader(stream);long count=0;
                while(reader.ReadLine() is { } line)
                {
                    var f=JsonSerializer.Deserialize<Frame>(line,ShadowSession.Json)??throw new InvalidDataException("Empty frame");
                    if(f.CpuPackage is null||f.CpuCoreMax is null||f.Gpu is null||f.Tz01 is null||f.Dtt3 is null)throw new InvalidDataException("Missing source object");
                    session.Add(f);count++;
                }
                if(count==0)throw new InvalidDataException("Replay contains no frames.");
                Console.WriteLine($"OEM shadow replay: {count} frames; summary: {Path.GetFullPath(args[2])}/summary.json");return 0;
            }
            Console.Error.WriteLine("Usage: --self-test | --replay input.jsonl[.gz] new-output-directory [--parameters file.json]");return 2;
        }
        catch(Exception ex){Console.Error.WriteLine("OEM shadow failed: "+ex.Message);return 1;}
    }
}
