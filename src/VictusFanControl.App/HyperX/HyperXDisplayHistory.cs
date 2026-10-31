using System.Text.Json;

namespace VictusFanControl.App;

// Local display history only. It cannot create a live acquisition or an alarm.
internal sealed record HyperXDisplayHistory(int SchemaVersion,int Percent,DateTimeOffset SampledAtUtc)
{
    internal static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VictusFanControl","hyperx-last-known.json");
    internal bool Valid(DateTimeOffset now)=>SchemaVersion==1&&Percent is >=0 and <=100&&SampledAtUtc!=default&&SampledAtUtc<=now;
    internal static HyperXDisplayHistory? Load(string path,out string? notice)
    {
        notice=null;if(!File.Exists(path))return null;
        try
        {
            var history=JsonSerializer.Deserialize<HyperXDisplayHistory>(File.ReadAllText(path));
            if(history?.Valid(DateTimeOffset.UtcNow)!=true)throw new InvalidDataException();
            return history;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {notice="No se pudo leer el último porcentaje guardado; se espera una lectura nueva.";return null;}
    }
    internal void Save(string path)
    {
        if(!Valid(DateTimeOffset.UtcNow))throw new InvalidDataException("Invalid HyperX display history.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(this));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
