using System.Text.Json;
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
        bool invalidNewest=false;try{ProductUpdates.ParseReleases(List(Release("v1.4.0",prerelease:true,digest:"bad"),Release("v1.2.0")),current,true);}catch(InvalidDataException){invalidNewest=true;}
        require(invalidNewest,"Preview channel silently bypassed invalid newest installer metadata.");
        var defaults=ProductProfilesStore.Serialize(new ProductProfiles());
        var legacy=defaults.Replace("  \"includePrereleaseUpdates\": false,\n", "");
        require(!legacy.Contains("includePrereleaseUpdates")&&!ProductProfilesStore.Parse(legacy).IncludePrereleaseUpdates,"Legacy preferences opted into previews.");
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
        Console.WriteLine("Product update policy: PASS (stable/preview channels, saved opt-in, version ordering, repository URL, digest, size and downgrade fences; no network/hardware IO).");
    }
}
