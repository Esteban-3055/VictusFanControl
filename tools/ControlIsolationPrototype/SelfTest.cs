using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace VfcIsolationFixture;

internal static class SelfTest
{
    internal static async Task<int> RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"vfc-isolation-fixture-"+Guid.NewGuid().ToString("N"));
        var pipe="vfc-isolation-fixture-"+Guid.NewGuid().ToString("N");
        var key=RandomNumberGenerator.GetBytes(32);var children=new List<Process>();
        static void Require(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
        Process Launch(params string[] args)
        {
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=AppContext.BaseDirectory};
            if(Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(SelfTest).Assembly.Location);
            foreach(var argument in args)start.ArgumentList.Add(argument);
            var child=Process.Start(start)??throw new IOException("Fixture child missing.");children.Add(child);
            child.StandardInput.AutoFlush=true;child.StandardInput.WriteLine(Convert.ToBase64String(key));return child;
        }
        async Task<string> Line(Process child)=>await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8))??throw new EndOfStreamException(await child.StandardError.ReadToEndAsync());
        using var parent=Process.GetCurrentProcess();
        Process Host()=>Launch("--fixture-host",pipe,root,parent.Id.ToString(),parent.StartTime.ToUniversalTime().Ticks.ToString());
        async Task<EngineState> Viewer()
        {
            var client=Launch("--fixture-view",pipe);var state=JsonSerializer.Deserialize<EngineState>(await Line(client))??throw new InvalidDataException();
            await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));Require(client.ExitCode==0,"Fixture viewer failed.");return state;
        }
        try
        {
            var host=Host();Require(await Line(host)=="READY","Fixture engine did not start.");
            var first=await Viewer();Require(first.Authenticated&&!first.Pending&&!first.HardwareAuthority,"First viewer acquired control or failed authentication.");
            var duplicate=Host();Require(await Line(duplicate)=="BLOCKED_LIVE_OWNER","A second fixture engine acquired a live owner's lease.");
            await duplicate.WaitForExitAsync();Require(duplicate.ExitCode==3,"Duplicate owner classification changed.");
            Request? captured=null;
            var authenticated=await Wire.ViewAsync(pipe,key,capture:request=>captured=request);
            Require(authenticated.Authenticated&&captured is not null,"Request capture fixture failed.");
            Require(!(await Wire.ViewAsync(pipe,key,replay:captured)).Authenticated,"A captured request replayed into a fresh challenge.");
            Require(!(await Wire.ViewAsync(pipe,key,invalidMac:true)).Authenticated,"Invalid HMAC was accepted.");
            Require(!(await Wire.ViewAsync(pipe,key,operation:"control")).CommandAllowed,"Prototype viewer obtained hardware capability.");
            host.StandardInput.WriteLine("hold");Require(await Line(host)=="ACK","Simulated operation did not enter pending state.");
            var second=await Viewer();Require(second.Engine==first.Engine&&second.OwnerPid==host.Id&&second.Pending&&!second.CommandAllowed,
                "Restarting the viewer replaced the engine or lost its pending operation.");
            host.StandardInput.WriteLine("return "+second.Engine+" "+second.Generation);Require(await Line(host)=="ACK","Exact simulated return proof was refused.");
            Require(!(await Viewer()).Pending,"Confirmed simulated return was not published.");
            host.StandardInput.WriteLine("hold");Require(await Line(host)=="ACK","Second simulated hold failed.");
            var statePath=Path.Combine(root,"fixture-state.json");var before=SHA256.HashData(await File.ReadAllBytesAsync(statePath));
            host.StandardInput.WriteLine("stop");await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));Require(host.ExitCode==0,"Fixture engine did not stop.");
            var replacement=Host();Require(await Line(replacement)=="BLOCKED_PENDING_RETURN","Process exit erased unconfirmed simulated state.");
            await replacement.WaitForExitAsync();
            var after=SHA256.HashData(await File.ReadAllBytesAsync(statePath));
            Require(replacement.ExitCode==3&&CryptographicOperations.FixedTimeEquals(before,after),
                "Blocked replacement changed retained evidence.");
            Console.WriteLine("PASS isolation prototype: separate engine and restarted viewer processes, CurrentUserOnly pipes, HMAC challenge/replay refusal, singleton engine, view-only capability, exact simulated return, retained pending evidence after owner exit; zero hardware references or commands.");
            return 0;
        }
        finally
        {
            foreach(var child in children)
            {
                try{if(!child.HasExited){child.StandardInput.Close();if(!child.WaitForExit(3000))child.Kill();}}
                finally{child.Dispose();}
            }
            CryptographicOperations.ZeroMemory(key);
            Require(Path.GetDirectoryName(Path.GetFullPath(root))==Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\','/'),"Unexpected fixture root.");
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
}
