using System.Text.Json;
using System.Text.Json.Nodes;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static class ProductUpdatesSelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        string Release(string tag="v1.2.0",bool draft=false,bool prerelease=false,string? url=null,string? digest=null,long size=4096,string state="uploaded",int count=1)
        {
            var name=$"VictusFanControl-{tag[1..]}-Setup-win-x64.exe";
            return JsonSerializer.Serialize(new {tag_name=tag,draft,prerelease,assets=Enumerable.Range(0,count).Select(_=>new{name,size,state,digest=digest??"sha256:"+new string('a',64),browser_download_url=url??$"https://github.com/{ProductUpdates.Repository}/releases/download/{tag}/{name}"})});
        }
        require(ProductRelease.NumericVersion==Version.Parse(ProductRelease.Version.Split('-')[0].Split('+')[0]),"Preview numeric version could not be read.");
        if(ProductRelease.IsPreview)
        {
            require(ProductUpdates.IsNewer(ProductRelease.NumericVersion,false,ProductRelease.NumericVersion),"Preview cannot promote to the same stable numeric version.");
            require(ProductUpdates.ParseRelease(Release("v"+ProductRelease.NumericVersion),ProductRelease.NumericVersion) is {IsPrerelease:false},"Same-version stable promotion was rejected by release parsing.");
            require(!ProductUpdates.IsNewer(ProductRelease.NumericVersion,true,ProductRelease.NumericVersion),"Same-number preview bypassed the no-downgrade fence.");
        }
        var current=new Version(1,1,0);
        require(ProductUpdates.ParseRelease(Release(),current)?.Version==new Version(1,2,0),"Stable newer release rejected.");
        require(ProductUpdates.ParseRelease(Release("v1.1.0"),current)is null&&ProductUpdates.ParseRelease(Release("v1.0.0"),current)is null,"Updater accepted equal version or downgrade.");
        require(ProductUpdates.ParseRelease(Release(draft:true),current)is null&&ProductUpdates.ParseRelease(Release(prerelease:true),current)is null,"Updater accepted draft/prerelease.");
        require(ProductUpdates.ParseRelease(Release(prerelease:true),current,true) is {IsPrerelease:true,Version:var previewVersion}&&previewVersion==new Version(1,2,0),"Opt-in rejected preview metadata.");
        require(ProductUpdates.ParseRelease(Release(draft:true,prerelease:true),current,true) is null,"Preview channel admitted a draft.");
        string List(params string[] releases)=>"["+string.Join(",",releases)+"]";
        var mixed=List(Release("v1.3.0",prerelease:true),Release("v1.2.0"),Release("v9.0.0",draft:true),Release("v1.1.1"));
        require(ProductUpdates.ParseReleases(mixed,current)?.Version==new Version(1,2,0),"Stable channel selected a preview or publication order over version order.");
        require(ProductUpdates.ParseReleases(mixed,current,true) is {IsPrerelease:true,Version:var latestPreview}&&latestPreview==new Version(1,3,0),"Preview channel failed to choose highest eligible version.");
        require(ProductUpdates.ParseReleases(List(Release("v1.3.0"),Release("v1.2.0",prerelease:true)),current,true) is {IsPrerelease:false,Version:var stableVersion}&&stableVersion==new Version(1,3,0),"Preview channel hid a newer stable version.");
        require(ProductUpdates.ParseReleases("[]",current,true)is null&&ProductUpdates.ParseReleases(List(Release("v1.1.0",prerelease:true)),current,true)is null,"Preview channel accepted equal/downgrade or mishandled empty releases.");
        var published=ProductUpdates.ParseRelease(Release(),current)!;
        require(ProductUpdates.ConfirmPublishedInstaller(published,published)==published,"Published cache confirmation rejected matching metadata.");
        foreach(var changed in new[]{published with{Sha256=new string('b',64)},published with{Size=published.Size+1},published with{Version=new Version(1,2,1)},null})
        {
            bool rejected=false;try{ProductUpdates.ConfirmPublishedInstaller(published,changed);}catch(InvalidDataException){rejected=true;}
            require(rejected,"Local cache metadata bypassed confirmation against the published GitHub installer.");
        }
        bool invalidNewest=false;try{ProductUpdates.ParseReleases(List(Release("v1.4.0",prerelease:true,digest:"bad"),Release("v1.2.0")),current,true);}catch(InvalidDataException){invalidNewest=true;}
        require(invalidNewest,"Preview channel silently bypassed invalid newest installer metadata.");
        var defaults=ProductProfilesStore.Serialize(new ProductProfiles());
        var legacy=JsonNode.Parse(defaults)!.AsObject();
        require(legacy.Remove("includePrereleaseUpdates")&&!ProductProfilesStore.Parse(legacy.ToJsonString()).IncludePrereleaseUpdates,"Legacy preferences opted into previews.");
        require(ProductProfilesStore.Parse(ProductProfilesStore.Serialize(new ProductProfiles{IncludePrereleaseUpdates=true})).IncludePrereleaseUpdates,"Preview preference did not survive save/load.");
        foreach(var json in new[]{Release("v1.2.0-beta"),Release(url:"https://evil.example/setup.exe"),Release(url:"http://github.com/setup.exe"),Release(digest:"sha256:"),Release(size:0),Release(size:ProductUpdates.MaximumInstallerBytes+1),Release(state:"new"),Release(count:0),Release(count:2)})
        {
            bool rejected=false;try{ProductUpdates.ParseRelease(json,current);}catch{rejected=true;}
            require(rejected,"Updater accepted malformed/untrusted release metadata.");
        }
        using var input=new MemoryStream(new byte[10]);using var output=new MemoryStream();
        bool bounded=false;try{ProductUpdates.CopyBoundedAsync(input,output,9,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidDataException){bounded=true;}
        require(bounded,"Unbounded update response accepted.");
        foreach(var automatic in new[]{false,true})foreach(var minimized in new[]{false,true})foreach(var startup in new[]{false,true})
        {
            var profiles=new ProductProfiles{ActivateAutomaticOnStart=automatic,StartMinimized=minimized,ExperimentalPlatformRetention=true,IncludePrereleaseUpdates=true};
            var prior=ProductProfilesStore.Serialize(profiles);
            var plan=ProductInstallationPlan.Create(profiles,true,startup);
            require(!plan.SaveProfiles&&plan.RegisterStartup==startup&&ProductProfilesStore.Serialize(plan.Profiles)==prior,"Update modified saved preferences or disabled/enabled startup.");
            var requested=ProductInstallationPlan.Create(profiles,true,startup,true);
            require(requested.SaveProfiles&&requested.RegisterStartup&&requested.Profiles.ActivateAutomaticOnStart&&
                ProductProfilesStore.Serialize(requested.Profiles with{ActivateAutomaticOnStart=automatic})==prior,"Explicit startup option changed unrelated preferences or failed to enable Automatic.");
            var uncheckedFresh=ProductInstallationPlan.Create(profiles,false,startup,false);
            require(uncheckedFresh.SaveProfiles&&!uncheckedFresh.RegisterStartup&&!uncheckedFresh.Profiles.ActivateAutomaticOnStart,"Unchecked fresh install enabled startup Automatic.");
            var fresh=ProductInstallationPlan.Create(profiles,false,startup);
            require(fresh.SaveProfiles&&fresh.RegisterStartup&&fresh.Profiles.ActivateAutomaticOnStart&&fresh.Profiles.StartMinimized&&
                fresh.Profiles.PerformanceConfiguration()==profiles.PerformanceConfiguration()&&fresh.Profiles.ExperimentalPlatformRetention==profiles.ExperimentalPlatformRetention,"Fresh installation changed fan/performance preferences.");
        }
        Task.Run(() => TestCache(require)).GetAwaiter().GetResult();
        Console.WriteLine("Product update policy: PASS (stable/preview channels, saved opt-in, version ordering, repository URL, digest, size and downgrade fences; no network/hardware IO).");
    }
    private static async Task TestCache(Action<bool,string> require)
    {
        var root=Path.Combine(Path.GetTempPath(),"Victus-cache-fixture-"+Guid.NewGuid().ToString("N"));
        var bytes=Enumerable.Range(0,8193).Select(i=>(byte)(i%251)).ToArray();
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        ProductUpdate Update(int patch=0,bool preview=false)=>new(new Version(1,2,patch),new Uri($"https://github.com/{ProductUpdates.Repository}/releases/download/v1.2.{patch}/VictusFanControl-1.2.{patch}-Setup-win-x64.exe"),bytes.Length,hash,preview);
        var update=Update();int downloads=0;
        Task Fetch(ProductUpdate _,Stream output,CancellationToken token,IProgress<long>? progress)
        {downloads++;return output.WriteAsync(bytes,token).AsTask();}
        async Task Reject(Func<Task> action,string message)
        {bool rejected=false;try{await action();}catch(Exception ex)when(ex is InvalidDataException or IOException or OperationCanceledException){rejected=true;}require(rejected,message);}
        try
        {
            var path=await ProductUpdates.DownloadCachedAsync(update,CancellationToken.None,root:root,fetch:Fetch);
            require(downloads==1&&File.Exists(path),"First verified download was not retained.");
            var retry=await ProductUpdates.DownloadCachedAsync(update,CancellationToken.None,root:root,fetch:Fetch);
            require(path==retry&&downloads==1,"Retry downloaded a complete installer again.");
            require(await ProductUpdates.FindCachedAsync(new Version(1,1,0),false,CancellationToken.None,root)==update,"A fresh process could not restore the verified download without network.");
            require(await ProductUpdates.FindCachedAsync(update.Version,true,CancellationToken.None,root) is null,"Cache offered an equal version/downgrade.");
            using(var opened=await ProductUpdates.OpenVerifiedInstallerAsync(update,CancellationToken.None,root))
                require(opened.Length==bytes.Length&&opened.Position==0,"Install verification did not keep the exact EXE open.");
            File.WriteAllBytes(path,new byte[bytes.Length]);
            await Reject(async()=>{using var opened=await ProductUpdates.OpenVerifiedInstallerAsync(update,CancellationToken.None,root);},"Modified saved installer was accepted.");
            require(await ProductUpdates.FindCachedAsync(new Version(1,1,0),false,CancellationToken.None,root) is null,"Restore admitted a corrupt installer.");
            await ProductUpdates.DownloadCachedAsync(update,CancellationToken.None,root:root,fetch:Fetch);
            require(downloads==2&&File.ReadAllBytes(path).SequenceEqual(bytes),"Corrupt cache could not be replaced by a newly verified download.");
            var preview=Update(1,true);await ProductUpdates.DownloadCachedAsync(preview,CancellationToken.None,root:root,fetch:Fetch);
            require(await ProductUpdates.FindCachedAsync(new Version(1,1,0),false,CancellationToken.None,root)==update&&
                await ProductUpdates.FindCachedAsync(new Version(1,1,0),true,CancellationToken.None,root)==preview,"Restored cache bypassed the preview channel.");
            // Interrupted writes and digest/size errors never publish an installable receipt.
            var bad=Update(2);
            await Reject(()=>ProductUpdates.DownloadCachedAsync(bad,CancellationToken.None,root:root,fetch:(_,o,t,_)=>o.WriteAsync(new byte[bytes.Length],t).AsTask()),"Bad digest was cached.");
            await Reject(()=>ProductUpdates.DownloadCachedAsync(bad,CancellationToken.None,root:root,fetch:(_,o,t,_)=>o.WriteAsync(bytes.AsMemory(0,100),t).AsTask()),"Incomplete installer was cached.");
            using(var cancelled=new CancellationTokenSource())
                await Reject(()=>ProductUpdates.DownloadCachedAsync(bad,cancelled.Token,root:root,fetch:async(_,o,t,_)=>{await o.WriteAsync(bytes.AsMemory(0,100),t);cancelled.Cancel();t.ThrowIfCancellationRequested();}),"Cancelled download was cached.");
            require(!Directory.EnumerateFiles(root,"*.partial-*",SearchOption.AllDirectories).Any()&&
                !Directory.EnumerateFiles(root,"*.tmp-*",SearchOption.AllDirectories).Any(),"Interrupted download left partial install candidates.");
            require(await ProductUpdates.FindCachedAsync(new Version(1,1,0),true,CancellationToken.None,root)==preview,"Download error discarded an earlier verified installer.");
            var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first=ProductUpdates.DownloadCachedAsync(bad,CancellationToken.None,root:root,fetch:async(_,o,t,_)=>{await gate.Task;await o.WriteAsync(bytes,t);});
            await Reject(()=>ProductUpdates.DownloadCachedAsync(bad,CancellationToken.None,root:root,fetch:Fetch),"Concurrent download acquired the same cache entry.");
            gate.SetResult();await first;
            // Metadata changes create a distinct entry; a same-version old digest cannot grant install admission.
            var changed=update with{Sha256=new string('a',64)};
            await Reject(async()=>{using var opened=await ProductUpdates.OpenVerifiedInstallerAsync(changed,CancellationToken.None,root);},"Cache ignored changed GitHub digest.");
            foreach(var invalid in new[]{update with{Download=new Uri("https://evil.example/setup.exe")},update with{Version=new Version(1,2)},update with{Size=0}})
                await Reject(()=>ProductUpdates.DownloadCachedAsync(invalid,CancellationToken.None,root:root,fetch:Fetch),"Cache accepted untrusted filesystem/network metadata.");
            var oldRoot=Path.Combine(root,"legacy");var oldDirectory=Path.Combine(oldRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(oldDirectory);
            File.WriteAllBytes(Path.Combine(oldDirectory,Path.GetFileName(path)),bytes);int before=downloads;
            var adopted=await ProductUpdates.DownloadCachedAsync(update,CancellationToken.None,root:oldRoot,fetch:Fetch);
            require(downloads==before&&adopted!=Path.Combine(oldDirectory,Path.GetFileName(path))&&await ProductUpdates.FindCachedAsync(new Version(1,1,0),false,CancellationToken.None,oldRoot)==update,
                "A complete v1.1.4 download was downloaded again or not given a verified receipt.");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(adopted)!,"download.json"),"{broken");
            require(await ProductUpdates.FindCachedAsync(new Version(1,1,0),false,CancellationToken.None,oldRoot) is null,"Malformed receipt granted install admission.");
            await ProductUpdates.DownloadCachedAsync(update,CancellationToken.None,root:oldRoot,fetch:Fetch);
            require(downloads==before,"Crash between EXE promotion and receipt forced another download.");
            Console.WriteLine("Persistent update cache: PASS (one download, restart/offline reuse, preview/downgrade fences, re-verification, corruption, partial/cancel cleanup, concurrency, old-download adoption and durable receipt repair; no network/hardware IO).");
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }

}
