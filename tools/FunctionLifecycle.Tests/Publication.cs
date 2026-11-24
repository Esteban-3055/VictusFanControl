using System.Text.Json;

static class PublicationProbe
{
    static async Task Main()
    {
        var results=new List<object>();
        foreach(var scenario in new[]{"fresh","expired","future","cache-not-renewed","failure-evidence-retained"})
        {
            try
            {
                var folder=Path.Combine(AppContext.BaseDirectory,"publication-files",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
                await using var client=(IAsyncDisposable)R.New("WmiFanGuiGuardianClient",true,folder,null,false,true);
                var at=DateTimeOffset.UtcNow.AddSeconds(scenario=="expired"?-6:scenario=="future"?30:scenario=="failure-evidence-retained"?-60:0);
                var failed=scenario=="failure-evidence-retained";
                var statusType=R.T("WmiFanGuiGuardianClient").GetProperty("Ctgp",R.Flags)!.PropertyType;
                var status=R.New(statusType,failed?"RecoveryRequired":"Active",!failed, true,failed?"Synthetic recovery evidence":null,at);
                var file=Path.Combine(folder,"ctgp-status.json");File.WriteAllText(file,JsonSerializer.Serialize(status,status.GetType()));
                var observed=R.Get(client,"Ctgp");bool passed;
                if(scenario=="cache-not-renewed")
                {
                    var first=observed is not null;File.Delete(file);await Task.Delay(5200);
                    observed=R.Get(client,"Ctgp");passed=first&&observed is null;
                }
                else passed=scenario is "expired" or "future"?observed is null:observed is not null;
                if(!passed||(bool)R.Get(client,"HasChild")!)throw new InvalidOperationException("Publication classification or no-child invariant failed.");
                var details=new{Passed=passed,ObservationPresent=observed is not null,NoChildCreated=true,Scope="Actual publication cache; own temporary files; no guardian, GUI or native calls."};
                results.Add(new{name=scenario,completed=true,details});Console.WriteLine(scenario+": "+JsonSerializer.Serialize(details));
            }
            catch(Exception e){results.Add(new{name=scenario,completed=false,error=e.ToString()});Console.WriteLine(scenario+": HARNESS ERROR "+e);}
        }
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"publication-results"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"publication-results","results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=results.Any(x=>x.GetType().GetProperty("completed")!.GetValue(x) is false)?1:0;
    }
}
