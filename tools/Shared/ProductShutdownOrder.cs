using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VictusFanControl.Scheduling;

/// <summary>Keep the GUI's IPC dependencies alive until it finishes session-end work.
/// Windows notifies higher levels first. This is unrelated to CPU priority.</summary>
internal static class ProductShutdownOrder
{
    internal const uint ApplicationLevel=0x3ff,GuardianLevel=0x100;
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessShutdownParameters(uint level,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessShutdownParameters(out uint level,out uint flags);

    internal static (uint Level,uint Flags) Read()
    {
        if(!GetProcessShutdownParameters(out var level,out var flags))throw new Win32Exception();
        return (level,flags);
    }
    internal static void Restore(uint level,uint flags)
    {if(!SetProcessShutdownParameters(level,flags))throw new Win32Exception();}

    internal static bool Apply(bool guardian,Action<string> log,
        Func<uint,uint,bool>? set=null,Func<(uint Level,uint Flags)>? read=null)
    {
        uint requested=guardian?GuardianLevel:ApplicationLevel;
        try
        {
            // Never use SHUTDOWN_NORETRY: retain the ordinary Windows user choice.
            if(!(set??SetProcessShutdownParameters)(requested,0))throw new Win32Exception();
            var actual=(read??Read)();
            bool confirmed=actual.Level==requested&&actual.Flags==0;
            log($"PRODUCT SHUTDOWN ORDER role={(guardian?"guardian":"application")} requested=0x{requested:X} effective=0x{actual.Level:X} flags={actual.Flags} confirmed={confirmed}");
            return confirmed;
        }
        catch(Exception error) when(error is Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            log("PRODUCT SHUTDOWN ORDER unconfirmed: "+error.Message);
            return false; // No authority, cleanup or journal change on failure.
        }
    }
}
