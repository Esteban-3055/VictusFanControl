using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VfcIsolationFixture;

// Fixture only: this project has NO hardware, VFC runtime, registry or driver references.
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if(args.SequenceEqual(new[]{"--self-test"}))return await SelfTest.RunAsync();
            if(args.Length==2&&args[0]=="--fixture-view")
            {
                var key=Convert.FromBase64String(await Wire.ReadAsync(Console.In,CancellationToken.None));
                try{Console.WriteLine(JsonSerializer.Serialize(await Wire.ViewAsync(args[1],key)));return 0;}
                finally{CryptographicOperations.ZeroMemory(key);}
            }
            if(args.Length==5&&args[0]=="--fixture-host"&&int.TryParse(args[3],out var owner)&&long.TryParse(args[4],out var started))
                return await HostAsync(args[1],args[2],owner,started);
            return 2;
        }
        catch(Exception error){Console.Error.WriteLine(error.GetType().Name+": "+error.Message);return 1;}
    }
    private static async Task<int> HostAsync(string pipe,string root,int ownerId,long ownerStarted)
    {
        var full=Path.GetFullPath(root);
        if(Path.GetDirectoryName(full)!=Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\','/')||
            !Path.GetFileName(full).StartsWith("vfc-isolation-fixture-",StringComparison.Ordinal)||
            !pipe.StartsWith("vfc-isolation-fixture-",StringComparison.Ordinal))return 2;
        using var owner=Process.GetProcessById(ownerId);
        if(owner.HasExited||owner.StartTime.ToUniversalTime().Ticks!=ownerStarted)return 197;
        Directory.CreateDirectory(full);
        if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)return 2;
        FileStream lease;
        try{lease=File.Open(Path.Combine(full,"engine.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){Console.WriteLine("BLOCKED_LIVE_OWNER");return 3;}
        using(lease)
        {
            var statePath=Path.Combine(full,"fixture-state.json");
            if(File.Exists(statePath))
            {
                var previous=JsonSerializer.Deserialize<EngineState>(await File.ReadAllTextAsync(statePath));
                if(previous is not {FixtureOnly:true,HardwareAuthority:false})return 2;
                if(previous.Pending){Console.WriteLine("BLOCKED_PENDING_RETURN");return 3;}
            }
            using var keyTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var key=Convert.FromBase64String(await Wire.ReadAsync(Console.In,keyTimeout.Token));
            if(key.Length!=32){CryptographicOperations.ZeroMemory(key);return 2;}
            using var current=Process.GetCurrentProcess();
            var state=new EngineState(Guid.NewGuid().ToString("N"),current.Id,current.StartTime.ToUniversalTime().Ticks,0,false);
            var sync=new object();using var lifetime=new CancellationTokenSource();
            void Persist(EngineState value)
            {
                using var file=new FileStream(statePath,FileMode.Create,FileAccess.Write,FileShare.Read);
                JsonSerializer.Serialize(file,value);file.Flush(true);
            }
            Persist(state);
            async Task Views()
            {
                while(!lifetime.IsCancellationRequested)
                {
                    using var stream=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await stream.WaitForConnectionAsync(lifetime.Token);
                    try
                    {
                        using var budget=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);budget.CancelAfter(2000);
                        using var reader=new StreamReader(stream,new UTF8Encoding(false),leaveOpen:true);
                        using var writer=new StreamWriter(stream,new UTF8Encoding(false),leaveOpen:true){AutoFlush=true};
                        EngineState observed;lock(sync)observed=state;
                        var challenge=new Challenge(1,observed.Engine,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                        await Wire.WriteAsync(writer,challenge,budget.Token);
                        var request=JsonSerializer.Deserialize<Request>(await Wire.ReadAsync(reader,budget.Token))??throw new InvalidDataException();
                        var authenticated=request.Version==1&&request.Sequence==1&&Guid.TryParseExact(request.Client,"N",out _)&&
                            Wire.Valid(key,Wire.RequestBody(challenge,request),request.Mac);
                        lock(sync)observed=state;
                        // Even authenticated prototype clients have view-only capability.
                        observed=observed with{Authenticated=authenticated,CommandAllowed=false};
                        var body=JsonSerializer.Serialize(observed);
                        await Wire.WriteAsync(writer,new SignedState(body,Wire.Mac(key,challenge.Nonce+"\n"+body)),budget.Token);
                    }
                    catch(Exception error) when(error is IOException or OperationCanceledException or JsonException){ }
                }
            }
            async Task Commands()
            {
                try
                {
                    while(!lifetime.IsCancellationRequested)
                    {
                        var line=await Wire.ReadAsync(Console.In,lifetime.Token);
                        if(line=="stop")return;
                        EngineState changed;
                        lock(sync)
                        {
                            if(line=="hold")state=state with{Generation=state.Generation+1,Pending=true};
                            else if(line=="return "+state.Engine+" "+state.Generation&&state.Pending)state=state with{Pending=false};
                            else throw new InvalidDataException("Only exact simulated return proof is accepted.");
                            changed=state;
                        }
                        Persist(changed);
                        Console.WriteLine("ACK");
                    }
                }
                catch(EndOfStreamException){ }
            }
            async Task MonitorOwner()
            {
                while(!lifetime.IsCancellationRequested)
                {await Task.Delay(250,lifetime.Token);if(owner.HasExited)return;}
            }
            var views=Views();var commands=Task.Run(Commands);var monitor=MonitorOwner();
            Console.WriteLine("READY");Console.Out.Flush();
            try{await Task.WhenAny(views,commands,monitor);}
            finally
            {
                lifetime.Cancel();
                try{await Task.WhenAll(views,commands,monitor);}catch(OperationCanceledException){ }
                CryptographicOperations.ZeroMemory(key);
            }
            return 0;
        }
    }
}
