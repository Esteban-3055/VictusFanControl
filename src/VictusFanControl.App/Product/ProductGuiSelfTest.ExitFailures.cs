using System.Reflection;
using System.Runtime.CompilerServices;
using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestExitFailureRegressions(Action<bool,string> require)
    {
        static void Set(ProductForm form,string name,object value) => typeof(ProductForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(form,value);
        var savedExit=Environment.ExitCode;
        var directory=Path.Combine(Path.GetTempPath(),"vfc-exit-failures-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach(var field in new[]{"_automaticRetryTask","_restartTask","_recoveryTask","_updateInstallTask","_startup","_externalCoolingStartup","cancel","live"})
            {
                Environment.ExitCode=savedExit;
                var runtime=new RecordingRuntime();
                using var form=new ProductForm("fixture://exit-regression",fixture:runtime,fixtureProfiles:new());
                form.Show();Application.DoEvents();
                using var cancellation=new CancellationTokenSource();
                using var callback=cancellation.Token.Register(()=>throw new IOException("Fixture callback failure"));
                var live=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if(field=="cancel")Set(form,"_updateCancellation",cancellation);
                else Set(form,field=="live"?"_automaticRetryTask":field,field=="live"?live.Task:Task.FromException(new IOException("Fixture completed failure")));
                var exit=form.RequestExitAsync();
                if(field=="live") { require(!exit.IsCompleted&&runtime.Disposals==0,"Live coordinator was abandoned.");live.SetResult(); }
                PumpUntil(()=>exit.IsCompleted,"Failed coordinator left exit pending: "+field);
                require(exit.IsCompletedSuccessfully&&runtime.Disposals==1&&runtime.Commands==0,"Final disposal skipped/duplicated after "+field);
                var again=form.RequestExitAsync();require(again.IsCompletedSuccessfully&&runtime.Disposals==1,"Cached exit repeated disposal.");
                require(Environment.ExitCode==savedExit,"Completed optional/coordinator fault was confused with failed native cleanup: "+field);
            }
            Environment.ExitCode=savedExit;
            var blocked=Path.Combine(directory,"parent-is-file");File.WriteAllText(blocked,"preserve");
            var recording=new RecordingRuntime();int writes=0;
            using(var form=new ProductForm("fixture://diagnostic-io-failure",fixture:recording,fixtureProfiles:new(),restartDirectory:blocked))
            {
                form.RecoverySelectionReader=()=>new(Guid.NewGuid(),Guid.Empty);
                form.RecoveryRunner=_=>{writes++;return Task.FromResult(new ProductRecoveryResult(false,"fixture","must not write"));};
                form.Show();Application.DoEvents();
                var recovery=form.ForceSessionRecoveryAsync(true);PumpUntil(()=>recovery.IsCompleted,"Failed diagnostic held recovery hostage.");
                require(recording.Disposals==1&&recording.Fences>=1&&writes==0&&File.ReadAllText(blocked)=="preserve",
                    "Diagnostic IO error skipped normal release, wrote recovery, or damaged original evidence.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Diagnostic failure prevented final exit.");
                require(recording.Disposals==1,"Diagnostic failure retried disposal.");
            }
            var profile=Path.Combine(directory,"oversized.json");
            var text=ProductProfilesStore.Serialize(new())+new string(' ',ProductProfilesStore.MaximumFileBytes);
            File.WriteAllText(profile,text);var bytes=File.ReadAllBytes(profile);
            var loaded=ProductProfilesStore.Load(profile,out var notice);
            require(notice?.StartsWith("Configuración no válida",StringComparison.Ordinal)==true&&!loaded.ActivateAutomaticOnStart&&File.ReadAllBytes(profile).SequenceEqual(bytes),
                "Oversized startup profile was accepted, activated Automatic or changed the original.");
            bool rejected=false;try{ProductProfilesStore.Parse(text);}catch(InvalidDataException){rejected=true;}
            require(rejected,"Oversized profile bypassed parser limit.");
            foreach(var changed in new[]{false,true})
            {
                // The actual commit transaction, without a hardware constructor.
                var owner=(ProductRuntime)RuntimeHelpers.GetUninitializedObject(typeof(ProductRuntime));
                var guardian=new WmiFanGuiGuardianClient(fixture:true,fixtureDirectory:Path.Combine(directory,"boundary-"+changed));
                typeof(ProductRuntime).GetField("_releaseCompleted",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(owner,true);
                typeof(ProductRuntime).GetField("_fanGuardian",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(owner,guardian);
                try { ProductRuntime.CommitReleasedBoundary(owner,_=>throw new IOException("Prepare failure"),()=>changed?"other-owner":guardian.SessionDirectory); }
                catch(IOException) { }
                bool refused=false;int commits=0;
                try { ProductRuntime.CommitReleasedBoundary(owner,_=>commits++); } catch(IOException) { refused=true; }
                require(changed?refused&&commits==0:!refused&&commits==1,"Boundary retry ignored commit ownership proof.");
                refused=false;try { ProductRuntime.CommitReleasedBoundary(owner,_=>commits++); } catch(IOException) { refused=true; }
                require(refused,"Committed boundary was reused.");
            }
            Console.WriteLine("PASS exit failure regressions: four coordinator faults, startup fault, external fault, cancellation callback, live ownership wait; one disposal and completed exit; diagnostic IO still releases without recovery writes; profiles bounded at 1 MiB and original retained; zero hardware IO.");
            Console.WriteLine("PASS constructor ownership transaction: pre-commit failure retains exact released boundary; changed/committed ownership refuses retry; no hardware constructor or IO.");
            using(var form=new ProductForm("fixture://cache-coordination",fixture:new RecordingRuntime(),fixtureProfiles:new()))
            {
                form.Show();Application.DoEvents();
                var pending=new TaskCompletionSource<ProductUpdate?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cache=form.RestoreDownloadedUpdateAsync(_=>pending.Task);
                require(!form.Canvas.UpdateBusy&&!form.Canvas.Busy,"Optional cache read owns the control/recovery coordinator.");
                var newer=FutureUpdate(1024);var cached=newer with{Version=new Version(newer.Version.Major,newer.Version.Minor+1,0)};
                CompleteGuiTask(form.CheckUpdateAsync(_=>Task.FromResult<ProductUpdate?>(newer)),"Interactive update query was blocked by cache.");
                var queryStatus=form.Canvas.UpdateStatus;
                pending.TrySetResult(cached);PumpUntil(()=>cache.IsCompleted,"Stale cache read did not retire.");
                require(form.Canvas.AvailableUpdate==newer&&form.Canvas.DownloadedUpdate is null&&form.Canvas.UpdateStatus==queryStatus,
                    "Late cache read replaced a newer interactive selection.");
                pending=new(TaskCreationOptions.RunContinuationsAsynchronously);cache=form.RestoreDownloadedUpdateAsync(_=>pending.Task);
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Read-only cache held exit hostage.");
                pending.TrySetResult(null);PumpUntil(()=>cache.IsCompleted,"Late cache completion failed to retire.");
            }
            foreach(var remainsUnavailable in new[]{false,true})
            {
                int registrations=0;var old=new RecordingRuntime();var next=new RecordingRuntime();
                using var form=new ProductForm("fixture://power-registration-recovery",fixture:old,fixtureProfiles:new(),registerPowerNotificationsInFixture:true,restartDirectory:directory);
                form.PowerRegistrationFixture=()=>{if(++registrations==1||remainsUnavailable)throw new IOException("Fixture power registration failed");};
                form.RecoveryRuntimeFactory=_=>Task.FromResult<IProductRuntime>(next);
                form.Show();PumpUntil(()=>old.Releases==1,"Initial notification failure did not release runtime.");
                var recovery=form.ForceSessionRecoveryAsync(true);PumpUntil(()=>recovery.IsCompleted,"Notification recovery held coordinator hostage.");
                require(registrations==2&&old.Disposals==1&&next.Starts==(remainsUnavailable?0:1),"Replacement started without power notifications or failed to retry registration.");
                if(remainsUnavailable)require(next.Disposals==1&&form.Canvas.State.LifecycleBlocked,"Missing replacement notifications escaped the release fence.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Notification regression exit failed.");
            }
            Console.WriteLine("PASS optional cache read never holds control/exit; power notification registration retried before replacement Start, persistent failure releases and blocks; zero hardware IO.");
        }
        finally { WaitOptionalDiagnostics();Environment.ExitCode=savedExit;Directory.Delete(directory,true); }
    }
}
