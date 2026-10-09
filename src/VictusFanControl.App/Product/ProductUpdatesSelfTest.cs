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
            var profiles=new ProductProfiles{ActivateAutomaticOnStart=automatic,StartMinimized=minimized,ExperimentalPlatformRetention=true};
            var prior=ProductProfilesStore.Serialize(profiles);
            var plan=ProductInstallationPlan.Create(profiles,true,startup);
            require(!plan.SaveProfiles&&plan.RegisterStartup==startup&&ProductProfilesStore.Serialize(plan.Profiles)==prior,"Update modified saved preferences or disabled/enabled startup.");
            var fresh=ProductInstallationPlan.Create(profiles,false,startup);
            require(fresh.SaveProfiles&&fresh.RegisterStartup&&fresh.Profiles.ActivateAutomaticOnStart&&fresh.Profiles.StartMinimized&&
                fresh.Profiles.PerformanceConfiguration()==profiles.PerformanceConfiguration()&&fresh.Profiles.ExperimentalPlatformRetention==profiles.ExperimentalPlatformRetention,"Fresh installation changed fan/performance preferences.");
        }
        Console.WriteLine("Product update policy: PASS (stable channel, repository URL, digest, size and downgrade fences; no network/hardware IO).");
    }
}
