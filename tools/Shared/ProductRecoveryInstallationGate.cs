using System.Security.Principal;

namespace VictusFanControl.Recovery;

internal sealed class ProductInstallationBusyException(string message,Exception? inner=null):IOException(message,inner);

/// <summary>Installation exclusion only; never proof of native owner release.</summary>
internal sealed class ProductRecoveryInstallationGate : IDisposable
{
    private FileStream? _file;
    private Mutex? _installer;
    private ProductRecoveryInstallationGate() { }
    private static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VictusFanControl");
    private static string Name {get{using var identity=WindowsIdentity.GetCurrent();return @"Local\VictusFanControl.Installation-"+identity.User!.Value;}}
    private static void RequireNoInstaller(string name)
    {
        using var gate=new Mutex(false,name);bool entered;
        try{entered=gate.WaitOne(0);}catch(AbandonedMutexException){entered=true;}
        if(!entered)throw new ProductInstallationBusyException("Hay una instalación activa. La sesión no adquirió hardware.");
        gate.ReleaseMutex();
    }
    internal static ProductRecoveryInstallationGate EnterApplication(string? root=null,string? name=null)
    {
        root??=Root;name??=Name;RequireNoInstaller(name);Directory.CreateDirectory(root);
        var lease=new ProductRecoveryInstallationGate();
        try
        {
            lease._file=File.Open(Path.Combine(root,"installation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);
            RequireNoInstaller(name);return lease;
        }
        catch(IOException error) when(error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
        {lease.Dispose();throw new ProductInstallationBusyException("Otra instancia o instalador conserva la exclusión.",error);}
        catch{lease.Dispose();throw;}
    }
    internal static ProductRecoveryInstallationGate EnterInstaller(string? root=null,string? name=null)
    {
        root??=Root;name??=Name;
        var lease=new ProductRecoveryInstallationGate();
        var mutex=new Mutex(false,name);bool entered;
        try{entered=mutex.WaitOne(0);}catch(AbandonedMutexException){entered=true;}
        if(!entered){mutex.Dispose();throw new IOException("Ya hay un instalador activo.");}
        lease._installer=mutex;
        try{Directory.CreateDirectory(root);lease._file=File.Open(Path.Combine(root,"installation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);return lease;}
        catch{lease.Dispose();throw;}
    }
    internal void HandFileToScript(){_file?.Dispose();_file=null;}
    public void Dispose(){_file?.Dispose();_file=null;if(_installer is {} gate){gate.ReleaseMutex();gate.Dispose();_installer=null;}}
}
