using System.Security.Cryptography;
using System.Text.Json;
using VictusFanControl.Product;

if(ProductRelease.Version!="1.2.9"||ProductRelease.IsPreview)throw new Exception("Wrong updater baseline.");
var current=new Version(1,2,9);
var expected=new Version(1,3,2);
var previousVersions=Enumerable.Range(9,9).Select(i=>new Version(1,2,i)).Append(new Version(1,3,0)).Append(new Version(1,3,1)).ToArray();
string Release(string tag,bool preview,bool draft=false)=>JsonSerializer.Serialize(new
{
    tag_name=tag,prerelease=preview,draft,
    assets=new[]{new{name="VictusFanControl-1.3.2-Setup-win-x64.exe",size=4096,state="uploaded",digest="sha256:"+new string('a',64),
        browser_download_url=$"https://github.com/{ProductUpdates.Repository}/releases/download/{tag}/VictusFanControl-1.3.2-Setup-win-x64.exe"}}
});
var metadata="["+Release("v1.3.2",false)+"]";
foreach(var previous in previousVersions)
foreach(var previews in new[]{false,true})
    if(ProductUpdates.ParseReleases(metadata,previous,previews) is not {Version:var v,IsPrerelease:false}||v!=expected)
        throw new Exception("Older version cannot discover stable 1.3.2.");
var preview="["+Release("v1.3.2",true)+"]";
if(ProductUpdates.ParseReleases(preview,current) is not null||ProductUpdates.ParseReleases(preview,current,true)?.Version!=expected)
    throw new Exception("Preview opt-in fence changed.");
if(ProductUpdates.ParseReleases("["+Release("v1.3.2-preview.1",true)+"]",current,true) is not null||
    ProductUpdates.ParseReleases("["+Release("v1.3.2",false,true)+"]",current,true) is not null||
    ProductUpdates.ParseReleases(metadata,expected,true) is not null)throw new Exception("Draft, suffix or installed-version fence changed.");
Console.WriteLine("PASS original 1.2.9 updater: stable numeric 1.3.2 eligible from 1.2.9 through 1.3.1 without preview opt-in; draft/suffix/preview fences preserved.");
if(args.Length==2&&args[0]=="--live")
{
    using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(10));
    var update=await ProductUpdates.CheckAsync(current,deadline.Token,false)??throw new Exception("Published stable unavailable to 1.2.9.");
    if(update.Version!=expected||update.IsPrerelease)throw new Exception("Unexpected published update.");
    foreach(var previous in previousVersions)
    foreach(var previews in new[]{false,true})
        if(await ProductUpdates.CheckAsync(previous,deadline.Token,previews)!=update)
            throw new Exception("Previous version cannot discover the same stable installer.");
    var root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
    var path=await ProductUpdates.DownloadCachedAsync(update,deadline.Token,root:root);
    await using(var verified=await ProductUpdates.OpenVerifiedInstallerAsync(update,deadline.Token,root))
        if(verified.Length!=update.Size)throw new Exception("Wrong download size.");
    var confirmed=await ProductUpdates.ConfirmPublishedInstallerAsync(update,current,false,deadline.Token);
    if(confirmed!=update)throw new Exception("Published installer confirmation changed.");
    var record=new{baseline="1.2.9",testedCurrentVersions=previousVersions.Select(v=>v.ToString()).ToArray(),baselineSource="e8a58a5cb01eb673114a8dca0c5c0e925f55fddc",version=update.Version.ToString(),update.IsPrerelease,
        previewOptInRequired=false,url=update.Download.ToString(),update.Size,update.Sha256,verifiedSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),installerExecuted=false};
    await File.WriteAllTextAsync(Path.Combine(root,"compatibility.json"),JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("PASS live original 1.2.9 updater: stable discovery, full installer download, SHA-256, cache and published metadata revalidation. Installer was not executed.");
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
