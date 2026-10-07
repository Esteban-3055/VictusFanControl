using VictusFanControl.OemShadow;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Runtime;
namespace VictusFanControl.OemShadowCapture;
internal static class CaptureTests
{
    public static int Run()
    {
        try
        {
            if(SelfTests.Run()!=0)return 1;
            void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            Require(Math.Abs(OemSources.DeciKelvinToC(3192)-46.05)<1e-8,"ACPI units");
            Require(OemSources.IsDtt(OemSources.DttPrefix+@"&REV_01\3&ABC&0&20_3",3),"qualified DTT3 participant");
            Require(!OemSources.IsDtt(@"PCI\VEN_8086&DEV_A71D&SUBSYS_88F8103C\ABC_3",3),"foreign subsystem refused");
            Require(!OemSources.IsDtt(OemSources.DttPrefix+@"\ABC_2",3),"wrong suffix refused");
            Require(!OemSources.IsDtt(OemSources.DttPrefix+@"F\ABC_3",3),"subsystem prefix collision refused");
            WmiOnlyInvestigationPolicy.Enable();
            WmiOnlyInvestigationPolicy.EnsureWmiRequestAllowed(Hp8C40BiosFanControl.BuildGetFanLevelRequest());
            void Denied(Action action)
            {bool denied=false;try{action();}catch(InvalidOperationException){denied=true;}Require(denied,"hardware isolation boundary did not reject access");}
            Denied(WmiOnlyInvestigationPolicy.EnsureDirectEcAllowed);
            Denied(()=>WmiOnlyInvestigationPolicy.EnsureWmiRequestAllowed(Hp8C40BiosFanControl.BuildSetFanLevelRequest(30,30)));
            Denied(()=>WmiOnlyInvestigationPolicy.EnsureWmiRequestAllowed(Hp8C40BiosFanControl.BuildLegacyDefaultRequest()));
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            var slot=new ReadSlot<Source>();int calls=0;
            Source Blocked(){Interlocked.Increment(ref calls);entered.Set();release.Wait();return new(42,DateTimeOffset.UtcNow);}
            slot.Poll(0,Blocked);Require(entered.Wait(5000),"fake query entered");
            try
            {
                for(int i=1;i<=50;i++)slot.Poll(i*1000,Blocked);
                Require(calls==1&&slot.InFlight,"blocked provider must not spawn replacement reads");
                slot.Invalidate();release.Set();Require(SpinWait.SpinUntil(()=>!slot.InFlight,5000),"fake query completes");
                slot.Poll(0,()=>new(50,DateTimeOffset.UtcNow));
                Require(slot.Latest is null,"old epoch completion discarded after discontinuity");
            }
            finally{release.Set();SpinWait.SpinUntil(()=>!slot.InFlight,5000);}
            Console.WriteLine("OEM capture self-test: PASS (qualified units/instances; bounded native admission; epoch invalidation; no hardware IO).");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine("OEM capture self-test: FAIL "+ex);return 1;}
    }
}
