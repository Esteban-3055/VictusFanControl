using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VictusFanControl.ExternalCooling;

namespace VictusFanControl.App;

internal sealed class ExternalCoolingSettingsStore(string? path=null)
{
    internal static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VictusFanControl","external-cooling.json");
    private readonly string _path=path??DefaultPath;
    private sealed record Stored
    {
        public required ExternalCoolingSettings Settings {get;init;}
        public string? ProtectedPairingKey {get;init;}
    }
    private Stored _stored=new(){Settings=new()};
    internal bool Paired=>_stored.ProtectedPairingKey is not null;
    internal ExternalCoolingSettings Load(out string? notice)
    {
        notice=null;
        try
        {
            if(!File.Exists(_path))return _stored.Settings;
            var info=new FileInfo(_path);if(info.Length>16384)throw new InvalidDataException();
            var loaded=JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path),ExternalCoolingProtocol.Json)??throw new InvalidDataException();
            loaded.Settings.Validate();_stored=loaded;return loaded.Settings;
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException or NullReferenceException)
        {notice="Configuración de base externa no válida; deshabilitada. Se conserva el archivo original.";_stored=new(){Settings=new()};return _stored.Settings;}
    }
    internal byte[]? ReadKey()
    {
        if(_stored.ProtectedPairingKey is null)return null;
        var key=WindowsPairingProtection.Unprotect(Convert.FromBase64String(_stored.ProtectedPairingKey));
        if(key.Length==32)return key;
        CryptographicOperations.ZeroMemory(key);throw new CryptographicException("Emparejamiento no disponible.");
    }
    internal void Save(ExternalCoolingSettings settings,string? newKey=null,bool removeKey=false)
    {
        settings.Validate();var encrypted=removeKey?null:_stored.ProtectedPairingKey;
        if(!string.IsNullOrWhiteSpace(newKey))
        {
            var key=ExternalCoolingProtocol.ParsePairingKey(newKey);
            try{encrypted=Convert.ToBase64String(WindowsPairingProtection.Protect(key));}
            finally{CryptographicOperations.ZeroMemory(key);}
        }
        var next=new Stored{Settings=settings,ProtectedPairingKey=encrypted};
        var full=Path.GetFullPath(_path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary=full+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,next,ExternalCoolingProtocol.Json);stream.Flush(true);}
            File.Move(temporary,full,true);_stored=next;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}

internal static class WindowsPairingProtection
{
    [StructLayout(LayoutKind.Sequential)]private struct Blob{public int Length;public IntPtr Data;}
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CryptProtectData(ref Blob input,string description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr handle);
    internal static byte[] Protect(byte[] bytes)=>Transform(bytes,true);
    internal static byte[] Unprotect(byte[] bytes)=>Transform(bytes,false);
    private static byte[] Transform(byte[] bytes,bool protect)
    {
        var input=new Blob{Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Blob output=default;
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            var ok=protect?CryptProtectData(ref input,"VictusFanControl ExternalCooling",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):
                CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!ok)throw new CryptographicException("Emparejamiento no disponible para este usuario de Windows.");
            var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);return result;
        }
        finally
        {
            for(int i=0;i<input.Length;i++)Marshal.WriteByte(input.Data,i,0);
            Marshal.FreeHGlobal(input.Data);
            if(output.Data!=IntPtr.Zero){for(int i=0;i<output.Length;i++)Marshal.WriteByte(output.Data,i,0);LocalFree(output.Data);}
        }
    }
}
