using System.Security.Cryptography;
using System.Text.Json;
using VictusFanControl.Product;

if(ProductRelease.Version!="1.2.9"||ProductRelease.IsPreview)throw new Exception("Wrong updater baseline.");
var current=new Version(1,2,9);
string Release(string tag,bool preview,bool draft=false)=>JsonSerializer.Serialize(new
{
    tag_name=tag,prerelease=preview,draft,
    assets=new[]{new{name="VictusFanControl-1.2.15-Setup-win-x64.exe",size=4096,state="uploaded",digest="sha256:"+new string('a',64),
        browser_download_url=$"https://github.com/{ProductUpdates.Repository}/releases/download/{tag}/VictusFanControl-1.2.15-Setup-win-x64.exe"}}
});
var metadata="["+Release("v1.2.15",true)+"]";
if(ProductUpdates.ParseReleases(metadata,current) is not null)throw new Exception("Stable channel admitted preview.");
if(ProductUpdates.ParseReleases(metadata,current,true) is not {Version:var version,IsPrerelease:true}||version!=new Version(1,2,15))throw new Exception("1.2.9 cannot discover numeric preview.");
if(ProductUpdates.ParseReleases("["+Release("v1.2.15-preview.7",true)+"]",current,true) is not null)throw new Exception("Baseline tag filter changed.");
if(ProductUpdates.ParseReleases("["+Release("v1.2.15",true,true)+"]",current,true) is not null)throw new Exception("Draft became eligible.");
if(ProductUpdates.ParseReleases(metadata,new Version(1,2,10),true)?.Version!=new Version(1,2,15))throw new Exception("1.2.10 cannot discover the next preview.");
if(ProductUpdates.ParseReleases(metadata,new Version(1,2,11),true)?.Version!=new Version(1,2,15))throw new Exception("1.2.11 cannot discover the next preview.");
if(ProductUpdates.ParseReleases(metadata,new Version(1,2,12),true)?.Version!=new Version(1,2,15))throw new Exception("1.2.12 cannot discover the next preview.");
if(ProductUpdates.ParseReleases(metadata,new Version(1,2,13),true)?.Version!=new Version(1,2,15))throw new Exception("1.2.13 cannot discover the next preview.");
if(ProductUpdates.ParseReleases(metadata,new Version(1,2,14),true)?.Version!=new Version(1,2,15))throw new Exception("1.2.14 cannot discover the next preview.");
Console.WriteLine("PASS original 1.2.9 updater: numeric v1.2.15 preview accepted with opt-in; stable channel, draft and suffix-tag fences preserved.");
if(args.Length==2&&args[0]=="--live")
{
    using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(10));
    var update=await ProductUpdates.CheckAsync(current,deadline.Token,true)??throw new Exception("Published preview unavailable to 1.2.9.");
    if(update.Version!=new Version(1,2,15)||!update.IsPrerelease)throw new Exception("Unexpected published update.");
    if(await ProductUpdates.CheckAsync(new Version(1,2,10),deadline.Token,true)!=update)throw new Exception("Previous preview cannot discover the same published installer.");
    if(await ProductUpdates.CheckAsync(new Version(1,2,11),deadline.Token,true)!=update)throw new Exception("1.2.11 cannot discover the same published installer.");
    if(await ProductUpdates.CheckAsync(new Version(1,2,12),deadline.Token,true)!=update)throw new Exception("1.2.12 cannot discover the same published installer.");
    if(await ProductUpdates.CheckAsync(new Version(1,2,13),deadline.Token,true)!=update)throw new Exception("1.2.13 cannot discover the same published installer.");
    if(await ProductUpdates.CheckAsync(new Version(1,2,14),deadline.Token,true)!=update)throw new Exception("1.2.14 cannot discover the same published installer.");
    if(await ProductUpdates.CheckAsync(current,deadline.Token,false) is not null)throw new Exception("Stable channel changed unexpectedly.");
    var root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
    var path=await ProductUpdates.DownloadCachedAsync(update,deadline.Token,root:root);
    await using(var verified=await ProductUpdates.OpenVerifiedInstallerAsync(update,deadline.Token,root))
        if(verified.Length!=update.Size)throw new Exception("Wrong download size.");
    var confirmed=await ProductUpdates.ConfirmPublishedInstallerAsync(update,current,true,deadline.Token);
    if(confirmed!=update)throw new Exception("Published installer confirmation changed.");
    var record=new{baseline="1.2.9",testedCurrentVersions=new[]{"1.2.9","1.2.10","1.2.11","1.2.12","1.2.13","1.2.14"},baselineSource="e8a58a5cb01eb673114a8dca0c5c0e925f55fddc",version=update.Version.ToString(),update.IsPrerelease,
        url=update.Download.ToString(),update.Size,update.Sha256,verifiedSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),installerExecuted=false};
    await File.WriteAllTextAsync(Path.Combine(root,"compatibility.json"),JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("PASS live 1.2.9 updater: discovery, preview opt-in, full installer download, SHA-256, retained cache and published metadata revalidation. Installer was not executed.");
}
else if(args.Length!=0)throw new ArgumentException("Use --live CACHE_ROOT or no arguments.");

// Only satisfies ProductRelease's unused target-name dependency; no controller is constructed.
namespace VictusFanControl.Hardware.Hp
{
    internal sealed class Hp8C40TargetProfile
    {
        internal static Hp8C40TargetProfile Instance {get;}=new();
        internal string Id=>"HP-8C40-9D0R1LA-F18";
    }
}
