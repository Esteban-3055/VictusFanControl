using VictusFanControl.Product;
using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

internal static class StabilityPipelineSelfTest
{
    internal static async Task RunAsync()
    {
        static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        ProductAutomaticRecoverySelfTest.Run(Require);
        var acquired=DateTimeOffset.UtcNow.AddMinutes(-1);
        for(var i=0;i<1000;i++)VictusFanControl.Runtime.RuntimeProgressJournal.Record("fixture-phase",generation:i,acquiredUtc:acquired);
        var history=VictusFanControl.Runtime.RuntimeProgressJournal.Capture();
        Require(history.Count==256&&history[^1].AcquiredUtc==acquired&&history[0].Sequence<history[^1].Sequence,"Passive phase journal exceeded its bound or changed acquisition dates.");
        var installationRoot=Path.Combine(Path.GetTempPath(),"vfc-installation-gate-"+Guid.NewGuid().ToString("N"));
        var installationName=@"Local\vfc-installation-gate-fixture-"+Guid.NewGuid().ToString("N");
        bool RefusedOnOtherThread(Action acquire)
        {
            var refused=false;Exception? unexpected=null;
            var thread=new Thread(()=>{try{acquire();}catch(IOException){refused=true;}catch(Exception error){unexpected=error;}}){IsBackground=true};
            thread.Start();Require(thread.Join(5000),"Installation fixture did not return.");
            if(unexpected is not null)throw unexpected;return refused;
        }
        try
        {
            using(var app=VictusFanControl.Recovery.ProductRecoveryInstallationGate.EnterApplication(installationRoot,installationName))
                Require(RefusedOnOtherThread(()=>{using var other=VictusFanControl.Recovery.ProductRecoveryInstallationGate.EnterInstaller(installationRoot,installationName);}),"Installer overlapped a live application.");
            using(var setup=VictusFanControl.Recovery.ProductRecoveryInstallationGate.EnterInstaller(installationRoot,installationName))
            {
                Require(RefusedOnOtherThread(()=>{using var other=VictusFanControl.Recovery.ProductRecoveryInstallationGate.EnterApplication(installationRoot,installationName);}),"Application overlapped installer recovery.");
                setup.HandFileToScript();
                using var script=File.Open(Path.Combine(installationRoot,"installation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                Require(RefusedOnOtherThread(()=>{using var other=VictusFanControl.Recovery.ProductRecoveryInstallationGate.EnterApplication(installationRoot,installationName);}),"Installation handoff admitted a controller.");
            }
            using var next=VictusFanControl.Recovery.ProductRecoveryInstallationGate.EnterApplication(installationRoot,installationName);
        }
        finally
        {
            Require(Path.GetDirectoryName(Path.GetFullPath(installationRoot))==Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\','/'),"Unexpected installation fixture root.");
            if(Directory.Exists(installationRoot))Directory.Delete(installationRoot,true);
        }
        foreach(var phase in Enumerable.Range(0,4))
        {
            var disposed=0;var failed=false;
            try{VictusFanControl.Runtime.InitializationCleanup.Run(()=>
            {for(var step=0;step<4;step++)if(step==phase)throw new IOException("initialization phase "+phase);return 1;},()=>disposed++);}
            catch(IOException){failed=true;}
            Require(failed&&disposed==1,"Partial initialization failed to release its acquired resource.");
        }
        var missing=await WindowsStartupRegistration.QueryAsync(false,readFixture:()=>Task.FromResult<string?>(null));
        Require(missing.State==StartupRegistrationState.Missing,"Confirmed missing task was not distinguished.");
        foreach(var error in new Exception[]{new UnauthorizedAccessException("denied"),new IOException("service unavailable"),new System.Xml.XmlException("invalid task")})
        {
            var unknown=await WindowsStartupRegistration.QueryAsync(false,readFixture:()=>Task.FromException<string?>(error));
            Require(unknown.State==StartupRegistrationState.Unknown,"Query error was reported as disabled or absent.");
        }
        using(var writer=new OptionalWorkQueue(4,128))
        using(var entered=new ManualResetEventSlim())
        using(var release=new ManualResetEventSlim())
        {
            try
            {
                writer.TryEnqueue(()=>{entered.Set();release.Wait();},32);
                Require(entered.Wait(5000),"Writer did not start.");
                for(var i=0;i<10000;i++)writer.TryEnqueue(()=>{},32);
                var status=writer.Status;
                Require(status.Entries<=4&&status.ReservedBytes<=128&&status.Dropped>=9996,"Optional writer exceeded its bounds.");
                Require(!await writer.FlushAsync(TimeSpan.FromMilliseconds(20)),"Blocked IO falsely reported drained.");
            }
            finally{release.Set();}
            Require(await writer.FlushAsync(TimeSpan.FromSeconds(5)),"Writer failed to drain.");
            writer.TryEnqueue(()=>throw new IOException("synthetic disk failure"),32);
            writer.TryEnqueue(()=>{},32);
            Require(await writer.FlushAsync(TimeSpan.FromSeconds(5))&&writer.Status.Failures==1,"Disk failure stopped optional writer.");
        }
        var callbacks=new Queue<Action>();var observed="";
        using(var mailbox=new LatestUiMailbox<string>(callbacks.Enqueue,value=>observed=value))
        {
            for(var i=0;i<10000;i++)mailbox.Post(i.ToString());
            Require(callbacks.Count==1,"UI accumulated callbacks.");callbacks.Dequeue()();
            Require(observed=="9999","UI did not receive the latest state.");
            mailbox.Post("late");mailbox.Dispose();callbacks.Dequeue()();
            Require(observed=="9999","Closed generation delivered a late state.");
        }
        using(var entered=new ManualResetEventSlim())
        using(var release=new ManualResetEventSlim())
        {
            using var pump=new LatestValuePump<string>(_=>{entered.Set();release.Wait();});
            try
            {
                pump.Post("first");Require(entered.Wait(5000),"Presentation did not start.");
                for(var i=0;i<10000;i++)pump.Post(i.ToString());
                pump.Dispose(); // Optional callbacks do not retain native shutdown.
            }
            finally{release.Set();}
        }
        var profiles=new ProductProfiles();var frozen=ProductProfilesStore.Freeze(profiles);
        ((IList<AdaptiveFanCurvePoint>)profiles.Ac.Fan.UnifiedDemand!.Curve)[0]=new(0,20);
        Require(frozen.Ac.Fan.UnifiedDemand!.Curve[0].Level!=20,"Applied profile shared the draft's mutable curve.");
        var blocked=false;
        try{((IList<AdaptiveFanCurvePoint>)frozen.Ac.Fan.UnifiedDemand.Curve)[0]=new(0,30);}
        catch(NotSupportedException){blocked=true;}
        Require(blocked,"Published applied profile was mutable.");
        using(var entered=new ManualResetEventSlim())
        using(var release=new ManualResetEventSlim())
        {
            Task<bool>? export=null;
            try
            {
                export=OptionalDiagnosticExport.TryStart(()=>{entered.Set();release.Wait();});
                Require(entered.Wait(5000),"Export did not start.");
                Require(!await OptionalDiagnosticExport.TryStart(()=>throw new Exception("duplicate")),"Blocked export allowed another worker.");
                Require(!export.IsCompleted,"Blocked export falsely completed.");
            }
            finally{release.Set();if(export is not null)await export;}
        }
        Console.WriteLine("PASS stability pipeline: held/failing disk, bounded reservations/drops, one UI callback/latest generation, held presentation, immutable applied profiles, single optional export; zero hardware IO.");
    }
}
