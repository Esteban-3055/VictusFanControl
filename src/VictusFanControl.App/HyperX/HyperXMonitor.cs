namespace VictusFanControl.App;

/// <summary>Independent optional USB reader. No WMI, EC, controller or audio recording.
/// Battery requests are rate limited; mute uses a blocking, cancellable HID read.</summary>
internal sealed class HyperXMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource stop=new();
    private readonly object gate=new();
    private HyperXSnapshot latest=new();
    private Task? worker;
    private FileStream? events;
    private CancellationTokenSource? eventStop;
    private Task? eventTask;
    private string? eventPath;
    private Task? disposal;
    private long muteVersion;
    private readonly System.Collections.Concurrent.ConcurrentQueue<HyperXSnapshot> muteEvents=new();
    internal IEnumerable<HyperXSnapshot> DrainMuteEvents(){while(muteEvents.TryDequeue(out var e))yield return e;}
    internal HyperXSnapshot Snapshot {get{lock(gate)return latest;}}
    private void Publish(Func<HyperXSnapshot,HyperXSnapshot> update){lock(gate)latest=update(latest);}
    internal void Start(){worker??=Task.Run(RunAsync);}
    private async Task RunAsync()
    {
        while(!stop.IsCancellationRequested)
        {
            try
            {
                var devices=HyperXHid.Enumerate();
                var batteries=devices.Where(d=>d.OutputBytes==20).ToArray();
                if(batteries.Length!=1)
                {
                    await StopEventsAsync();
                    Publish(_=>new(){Status=batteries.Length==0?"Receptor no disponible":"Varios receptores: no se selecciona uno arbitrariamente"});
                }
                else
                {
                    var battery=batteries[0];
                    // Match the event collection to this receiver, never another device.
                    int collection=battery.Path.IndexOf("&col",StringComparison.OrdinalIgnoreCase);
                    var prefix=collection<0?battery.Path:battery.Path[..collection];
                    var ev=devices.FirstOrDefault(d=>d.Page==0xffc0&&d.Path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase));
                    if(ev?.Path!=eventPath||eventTask?.IsCompleted==true)
                    {
                        await StopEventsAsync();
                        if(ev is not null)
                        {
                            try
                            {
                                events=HyperXHid.Open(ev,false);eventPath=ev.Path;
                                eventStop=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                                eventTask=ListenAsync(events,eventStop.Token);
                            }
                            catch(Exception ex)when(ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                            {await StopEventsAsync();}
                        }
                    }
                    Publish(s=>s with{ReceiverPresent=true,Status="Consultando batería"});
                    using var file=HyperXHid.Open(battery,true);
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    byte[] request=new byte[20];request[0]=0x21;request[1]=0xff;request[2]=5;
                    await file.WriteAsync(request,deadline.Token);
                    var buffer=new byte[battery.InputBytes];
                    while(true)
                    {
                        int count=await file.ReadAsync(buffer,deadline.Token);
                        if(count==0)throw new IOException("Receptor desconectado");
                        if(HyperXSnapshot.Decode(buffer.AsSpan(0,count)) is not{} b)continue;
                        Publish(s=>s.Powered==false?s with{Status="Audífonos apagados",Percent=null,Millivolts=null,Charging=null,SampledAt=null}:
                            s with{Status=eventPath is null?"Batería recibida; eventos de micrófono no disponibles":"Lectura recibida",Millivolts=b.Millivolts,Percent=b.Percent,Charging=b.Charging,SampledAt=DateTimeOffset.UtcNow});break;
                    }
                }
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
            catch(Exception ex)when(ex is not OutOfMemoryException)
            {AppLog.Write("HyperX optional reader failed: "+ex.GetType().Name);Publish(s=>s with{Status="Sin respuesta actual de los audífonos",Percent=null,Millivolts=null,Charging=null,SampledAt=null});}
            try{await Task.Delay(TimeSpan.FromSeconds(HyperXSnapshot.PollSeconds),stop.Token);}
            catch(OperationCanceledException){break;}
        }
        await StopEventsAsync();
    }
    private async Task ListenAsync(FileStream file,CancellationToken token)
    {
        try
        {
            var b=new byte[2];
            while(!token.IsCancellationRequested)
            {
                int n=await file.ReadAsync(b,token);if(n==0)break;
                if(n!=2)continue;
                ApplyEvent(b[0],b[1]);
            }
        }
        catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException){}
        finally{Publish(s=>s with{Muted=null,Powered=null,Percent=null,Millivolts=null,Charging=null,SampledAt=null});}
    }
    internal void ApplyEvent(byte kind,byte value)
    {
        // Never interpret unrelated 2-byte reports as "unmuted".
        if(kind==0x65&&value is 0 or 4)
        {
            Publish(s=>s with{Muted=value==4,MuteEventVersion=Interlocked.Increment(ref muteVersion),MuteEventAt=DateTimeOffset.UtcNow});
            muteEvents.Enqueue(Snapshot);while(muteEvents.Count>32)muteEvents.TryDequeue(out _);
        }
        if(kind==0x64&&value is 1 or 3)Publish(s=>value==1?s with{Powered=true}:s with{Powered=false,Muted=null,Percent=null,Millivolts=null,Charging=null,SampledAt=null});
    }
    private async Task StopEventsAsync()
    {
        eventStop?.Cancel();events?.Dispose();
        if(eventTask is not null)try{await eventTask;}catch(Exception e)when(e is not OutOfMemoryException){AppLog.Write("HyperX event reader cleanup: "+e.GetType().Name);}
        eventStop?.Dispose();eventStop=null;events=null;eventTask=null;eventPath=null;
    }
    public ValueTask DisposeAsync()
    {lock(gate)return new ValueTask(disposal??=DrainOptionalAsync(StopAsync,TimeSpan.FromSeconds(3)));}
    internal static async Task DrainOptionalAsync(Func<Task> stopAction,TimeSpan budget)
    {
        // Optional USB telemetry owns no fan/performance authority. Never make
        // Windows exit or the UI wait indefinitely on a stuck device driver.
        var cleanup=Task.Run(stopAction);
        try{await cleanup.WaitAsync(budget);}
        catch(TimeoutException)
        {
            AppLog.Write("HyperX optional shutdown exceeded its budget; control authority is unaffected.");
            _=cleanup.ContinueWith(t=>AppLog.Write("HyperX deferred cleanup: "+t.Exception?.GetBaseException().GetType().Name),
                CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {AppLog.Write("HyperX optional cleanup failed: "+e.GetType().Name);}
    }
    private async Task StopAsync()
    {
        stop.Cancel();if(worker is not null)await worker;stop.Dispose();
    }
}
