using System.Reflection;
using System.Text.Json;

// These checks assert corrected outcomes, not merely completion of a probe.
static class AcceptanceRunner
{
    static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);}
    static bool B(JsonElement d,string p)=>d.GetProperty(p).GetBoolean();
    static int N(JsonElement d,string p)=>d.GetProperty(p).GetInt32();
    static bool Null(JsonElement d,string p)=>d.GetProperty(p).ValueKind==JsonValueKind.Null;
    static string S(JsonElement d,string p)=>d.GetProperty(p).GetString()!;
    static bool Same(JsonElement d,string a,string b)=>d.GetProperty(a).GetRawText()==d.GetProperty(b).GetRawText();
    [STAThread]
    static async Task<int> Main()
    {
        try
        {
            var groups=new[]{("followup",typeof(FollowupProbe),13),("extended",typeof(ExtendedProbe),11),
                ("edge",typeof(EdgeProbe),17),("reentry",typeof(ReentryProbe),8),
                ("pure",typeof(PureProbe),6),("linked",typeof(LinkedProbe),12),
                ("publication",typeof(PublicationProbe),5),("settings",typeof(SettingsProbe),2)};
            int cases=0;
            foreach(var (name,type,count) in groups)
            {
                Environment.ExitCode=0;
                var invocation=type.GetMethod("Main",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,null);
                if(invocation is Task task)await task;
                Require(Environment.ExitCode==0,name+": harness error");
                using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,name+"-results","results.json")));
                Require(document.RootElement.GetArrayLength()==count,name+": missing cases");
                foreach(var row in document.RootElement.EnumerateArray())
                {
                    var label=S(row,"name");Require(B(row,"completed"),label+": did not complete");
                    Check(label,row.GetProperty("details"));cases++;
                }
                Console.WriteLine($"PASS {name}: {count} corrected outcomes.");
            }
            Console.WriteLine($"PASS {cases} lifecycle outcomes plus 1812 profile mutations; fixture IPC only, no desktop or physical IO.");
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static void Check(string name,JsonElement d)
    {
        bool ok;
        if(name.StartsWith("E01"))ok=Same(d,"Expected","StoredBefore")&&Same(d,"Expected","StoredAfter")&&!B(d,"DomainLatchedFault")&&B(d,"ActiveAfterSourceReturns")&&Same(d,"Expected","AppliedValue");
        else if(name.StartsWith("E02")||name.StartsWith("H02"))ok=B(d,"Active")&&Same(d,"Expected","Actual");
        else if(name.StartsWith("E03"))ok=B(d,"FixtureLaunchRefused")&&B(d,"FaultBefore")&&B(d,"FaultAfter")&&N(d,"LaunchAttempts")==1&&!B(d,"Active");
        else if(name.StartsWith("E04"))ok=!B(d,"Fault")&&B(d,"AnyPerformanceChild")&&d.GetProperty("Requested").EnumerateObject().Any(x=>x.Value.GetBoolean());
        else if(name.StartsWith("E05"))ok=N(d,"Cases")==1812&&N(d,"OriginalFilesAltered")==0&&d.GetProperty("UnexpectedErrors").GetArrayLength()==0;
        else if(name.StartsWith("F04"))ok=!B(d,"Active")&&B(d,"SamePreBoundarySnapshot")&&N(d,"PostResumeAcquisitions")==0;
        else if(name.StartsWith("F01"))ok=Null(d,"EarlyError")&&!B(d,"Fault")&&B(d,"ActiveAfterHealthy");
        else if(name.StartsWith("F02")){var b=d.GetProperty("BeforeLaterPowerNotification");ok=!B(b,"Maintenance")&&!B(b,"PerformanceBlocked")&&Null(b,"RequestFailure")&&!B(d,"MaintenanceAfterLaterResume")&&B(d,"CpuActiveAfterReconcile");}
        else if(name.StartsWith("F03"))ok=B(d,"OwnerKept")&&N(d,"GlobalCloses")==0&&B(d,"CpuOwnerPresent");
        else if(name.StartsWith("G04")){var waiting=S(d,"Binding")=="FollowAutomatic"&&S(d,"Mode")=="Manual";var active=d.GetProperty("ActualActive").ValueKind==JsonValueKind.True;ok=waiting?!active&&S(d,"PresentedState")!="Active":active&&B(d,"ActualManaged")&&S(d,"PresentedState")=="Active";}
        else if(name.StartsWith("G03"))ok=!B(d,"WmiChildWasStarted")&&!B(d,"CtgpFaulted")&&!B(d,"FaultAfterOff")&&Null(d,"ReplacementFailure")&&B(d,"CpuStillActive");
        else if(name.StartsWith("G01"))ok=!B(d,"ChildAtCancellation")&&!B(d,"FaultAtCancellation")&&!B(d,"FaultAfterOff")&&Null(d,"RetryFailure")&&B(d,"ChildAfterExplicitRetry");
        else if(name.StartsWith("G02"))ok=B(d,"RuntimeCommittedNewRules")&&B(d,"SavedNewRules")&&B(d,"CpuStillActive")&&B(d,"AutomaticTracksActual")&&(!B(d,"UnsupportedCtgpRequested")||S(d,"Failure").Contains("guardadas"));
        else if(name.StartsWith("H01")){var active=!B(d,"UseDraftAtConstruction")&&B(d,"SavedStartupOptin");ok=B(d,"DirtyPreserved")&&B(d,"Active")==active&&(active?Same(d,"ActualValue","SavedValue"):Null(d,"ActualValue"));}
        else if(name.StartsWith("H03"))ok=!B(d,"FaultAfterLinkedEntry")&&B(d,"ActiveAfterLinkedEntry")&&B(d,"ExplicitDomainRetryWorks");
        else if(name.StartsWith("I01")){var fresh=S(d,"Scenario")=="fresh";ok=B(d,"RequestedRuntimeReplacement")==fresh&&N(d,"BudgetAttempts")== (fresh?1:0);}
        else if(name.StartsWith("I02"))ok=N(d,"InitialFullNotices")==1&&N(d,"ReturnToFullNotices")==1;
        else if(name.StartsWith("I03"))ok=N(d,"InitialNotices")==1&&N(d,"IdenticalTickNotices")==0&&N(d,"LcdDurationChangeNotices")==0;
        else if(name.StartsWith("K01"))ok=Same(d,"SavedModelMode","DiskMode")&&Same(d,"VisibleModeAfterDiscard","DiskMode")&&!B(d,"DirtyAfterSave")&&!B(d,"DirtyAfterDiscard")&&B(d,"ModelAndFileAgreeAfterDiscard")&&!B(d,"DesktopHandlesCreated")&&N(d,"TelemetryCaptures")==0&&(!B(d,"ConnectionFailureInjected")||S(d,"Notice").Contains("guardadas"));
        else ok=B(d,"Passed")&&B(d,"NoChildCreated");
        Require(ok,name+": corrected behavior not observed: "+d.GetRawText());
    }
}
