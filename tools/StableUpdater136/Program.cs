using System.Security.Cryptography;
using System.Text.Json;
using VictusFanControl.Product;

if(ProductRelease.Version!="1.2.9"||ProductRelease.IsPreview)throw new Exception("Wrong updater baseline.");
if(args.Length!=2)throw new ArgumentException("Cache root and public release metadata required.");
var expected=new Version(1,3,6);var oldest=new Version(1,2,9);
using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(10));
var stable=await ProductUpdates.CheckAsync(oldest,deadline.Token,false)??throw new Exception("Stable channel unavailable.");
if(stable.Version!=expected||stable.IsPrerelease||stable.Size!=168918034||
    stable.Sha256!="c4defaedc04bf3bd0809bc5055c853269c2256c85f93012337bb02f8f1bb0fe8")
    throw new Exception("Published stable differs from exact CI installer.");
if(await ProductUpdates.CheckAsync(oldest,deadline.Token,true)!=stable)
    throw new Exception("Preview opt-in does not select the same stable release.");
var versions=Enumerable.Range(9,9).Select(i=>new Version(1,2,i)).Concat(Enumerable.Range(0,6).Select(i=>new Version(1,3,i))).ToArray();
var metadata=await File.ReadAllTextAsync(args[1],deadline.Token);
foreach(var previous in versions)
foreach(var previews in new[]{false,true})
    if(ProductUpdates.ParseReleases(metadata,previous,previews)!=stable)
        throw new Exception("Original updater cannot select the same stable installer.");
if(ProductUpdates.ParseReleases(metadata,expected,false) is not null||ProductUpdates.ParseReleases(metadata,expected,true) is not null)
    throw new Exception("Already-current version was offered an update.");
var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
var path=await ProductUpdates.DownloadCachedAsync(stable,deadline.Token,root:root);
await using(var verified=await ProductUpdates.OpenVerifiedInstallerAsync(stable,deadline.Token,root))
    if(verified.Length!=stable.Size)throw new Exception("Wrong installer length.");
if(await ProductUpdates.ConfirmPublishedInstallerAsync(stable,oldest,false,deadline.Token)!=stable)
    throw new Exception("Public installer metadata changed.");
var hash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path,deadline.Token))).ToLowerInvariant();
if(hash!=stable.Sha256)throw new Exception("Installer hash changed.");
var record=new{schemaVersion=1,baseline="1.2.9",baselineSource="e8a58a5cb01eb673114a8dca0c5c0e925f55fddc",
    sourceHead="a83db258c4541df65f22617c990c1fa5cbdf9dfa",version=stable.Version.ToString(),stable.IsPrerelease,
    previewOptInRequired=false,stableRelease=stable.Version.ToString(),testedCurrentVersions=versions.Select(v=>v.ToString()).ToArray(),
    url=stable.Download.ToString(),stable.Size,stable.Sha256,verifiedSha256=hash,installerExecuted=false};
await File.WriteAllTextAsync(Path.Combine(root,"compatibility.json"),JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}),deadline.Token);
Console.WriteLine("PASS original 1.2.9 public updater: stable 1.3.6 from 1.2.9 through 1.3.5 without preview opt-in; full download/hash/cache/public metadata verified; installer not executed.");

namespace VictusFanControl.Hardware.Hp
{
    internal sealed class Hp8C40TargetProfile
    {
        internal static Hp8C40TargetProfile Instance {get;}=new();
        internal string Id=>"HP-8C40-9D0R1LA-F18";
    }
}
