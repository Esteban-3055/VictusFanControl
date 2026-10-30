using System.Security.Cryptography;
using System.Text.Json;
using VictusFanControl.Product;

if(ProductRelease.Version!="1.2.9"||ProductRelease.IsPreview)throw new Exception("Wrong updater baseline.");
if(args.Length!=2)throw new ArgumentException("Cache root and public release metadata required.");
var expected=new Version(1,3,13);var stableVersion=new Version(1,3,6);var oldest=new Version(1,2,9);
using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(10));
var stable=await ProductUpdates.CheckAsync(oldest,deadline.Token,false)??throw new Exception("Stable channel unavailable.");
if(stable.Version!=stableVersion||stable.IsPrerelease)throw new Exception("Stable channel changed.");
var preview=await ProductUpdates.CheckAsync(oldest,deadline.Token,true)??throw new Exception("Preview unavailable.");
if(preview.Version!=expected||!preview.IsPrerelease||preview.Size!=long.Parse(Environment.GetEnvironmentVariable("VERIFIED_INSTALLER_SIZE")??throw new Exception("Missing CI installer size."))||
    preview.Sha256!=(Environment.GetEnvironmentVariable("VERIFIED_INSTALLER_SHA256")??throw new Exception("Missing CI installer hash.")))
    throw new Exception("Published preview differs from exact CI installer.");
var versions=Enumerable.Range(9,9).Select(i=>new Version(1,2,i)).Concat(Enumerable.Range(0,13).Select(i=>new Version(1,3,i))).ToArray();
var metadata=await File.ReadAllTextAsync(args[1],deadline.Token);
foreach(var previous in versions)
{
    var without=ProductUpdates.ParseReleases(metadata,previous,false);
    if(previous>=stableVersion?without is not null:without?.Version!=stableVersion||without.IsPrerelease)
        throw new Exception("Preview was offered without opt-in or stable changed.");
    if(ProductUpdates.ParseReleases(metadata,previous,true)!=preview)throw new Exception("Original updater cannot select the same preview.");
}
var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
var path=await ProductUpdates.DownloadCachedAsync(preview,deadline.Token,root:root);
await using(var verified=await ProductUpdates.OpenVerifiedInstallerAsync(preview,deadline.Token,root))
    if(verified.Length!=preview.Size)throw new Exception("Wrong installer length.");
if(await ProductUpdates.ConfirmPublishedInstallerAsync(preview,oldest,true,deadline.Token)!=preview)
    throw new Exception("Public installer metadata changed.");
var hash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path,deadline.Token))).ToLowerInvariant();
if(hash!=preview.Sha256)throw new Exception("Installer hash changed.");
var record=new{schemaVersion=1,baseline="1.2.9",baselineSource="e8a58a5cb01eb673114a8dca0c5c0e925f55fddc",
    sourceHead="7a006fdddb0a006440274315cd86d5e4d49621a4",version=preview.Version.ToString(),preview.IsPrerelease,
    previewOptInRequired=true,stableReleaseUnchanged=stable.Version.ToString(),testedCurrentVersions=versions.Select(v=>v.ToString()).ToArray(),
    url=preview.Download.ToString(),preview.Size,preview.Sha256,verifiedSha256=hash,installerExecuted=false};
await File.WriteAllTextAsync(Path.Combine(root,"compatibility.json"),JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}),deadline.Token);
Console.WriteLine("PASS original 1.2.9 public updater: 1.3.13 preview opt-in, stable 1.3.6 preserved, full download/hash/cache/public metadata verified; installer not executed.");

namespace VictusFanControl.Hardware.Hp
{
    internal sealed class Hp8C40TargetProfile
    {
        internal static Hp8C40TargetProfile Instance {get;}=new();
        internal string Id=>"HP-8C40-9D0R1LA-F18";
    }
}
