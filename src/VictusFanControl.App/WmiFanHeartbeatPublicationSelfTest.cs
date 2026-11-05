using System.Text.Json;

namespace VictusFanControl.App;

internal static class WmiFanHeartbeatPublicationSelfTest
{
    internal static async Task RunAsync()
    {
        var directory=Path.GetFullPath(Path.Combine("logs/heartbeat-publication-self-test",Guid.NewGuid().ToString("N")));
        await using var client=new WmiFanGuiGuardianClient(fixture:true,fixtureDirectory:directory);
        client.Heartbeat();
        var path=Path.Combine(directory,"heartbeat.json");
        using var stop=new CancellationTokenSource();using var barrier=new Barrier(2);
        var reader=Task.Run(()=>
        {
            long last=-1;int reads=0;
            while(!stop.IsCancellationRequested)
            {
                try
                {
                using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
                using var json=JsonDocument.Parse(stream);
                var root=json.RootElement;var elapsed=root.GetProperty("ElapsedMs").GetInt64();
                if(root.GetProperty("Pid").GetInt32()!=Environment.ProcessId||elapsed<last)
                    throw new InvalidOperationException("Concurrent publication regressed time or changed owner.");
                last=elapsed;reads++;Thread.Yield();
                }
                catch(FileNotFoundException) { } // Same transient pathname gap accepted by the production observer.
                catch(IOException e) when(e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)) { }
            }
            return reads;
        });
        try
        {
            await Task.WhenAll(Enumerable.Range(0,2).Select(_=>Task.Run(()=>
            {
                for(int i=0;i<256;i++)
                {
                    if(!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))throw new TimeoutException("Heartbeat writers did not rendezvous.");
                    client.Heartbeat();
                }
            })));
        }
        finally{stop.Cancel();}
        if(await reader==0)throw new InvalidOperationException("Concurrent reader never observed a publication.");
        // The lock is not a retry/suppression policy: genuine publication failure
        // still reaches the caller and cannot pretend to renew the guardian lease.
        var before=File.ReadAllText(path);bool rejected=false;
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
            try{client.Heartbeat();}catch(IOException){rejected=true;}
        if(!rejected||File.ReadAllText(path)!=before||Directory.GetFiles(directory,"*.tmp").Length!=0)
            throw new InvalidOperationException("Failed heartbeat was swallowed, changed the lease or leaked a temporary file.");
        client.Heartbeat();
        Console.WriteLine("PASS actual guardian heartbeat: 512 synchronized concurrent publications, shared-delete reader, complete JSON and monotonic owner timestamps; real I/O failure still propagates without a false renewal; zero hardware IO.");
    }
}
