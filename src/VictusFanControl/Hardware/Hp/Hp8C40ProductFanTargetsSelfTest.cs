using VictusFanControl.Control;
using VictusFanControl.Runtime;

namespace VictusFanControl.Hardware.Hp;

internal static class Hp8C40ProductFanTargetsSelfTest
{
    internal static void Run()
    {
        static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        static void Reject(Action action){try{action();}catch(HpBiosCallException){return;}catch(ArgumentException){return;}catch(InvalidOperationException){return;}throw new InvalidOperationException("Invalid preview operation accepted.");}
        var calls=new List<HpBiosRequest>();var intents=new List<int>();
        var session=new WmiFanSession(r=>{calls.Add(r);return 0;},intents.Add,10,true);
        foreach(var level in new[]{10,50,51,52,53,54,53,52,50})
        {
            var pair=Hp8C40ProductFanTargets.FromLogical(level);
            Check(session.Apply(level,true),"Preview target change lost.");
            Check(calls[^1].Payload.SequenceEqual(new byte[]{(byte)pair.Cpu,(byte)pair.Gpu,0,0}),"Wrong physical preview pair.");
            Check(pair.Gpu<=52,"GPU preview exceeded 52.");
            Check(WmiFanExperimentBoundary.IsAllowed(calls[^1],true,false,false,gui:true,productUpperRange:true),"Preview whitelist denied permitted pair.");
            Check(!session.Apply(level,true),"Unchanged preview target retransmitted.");
        }
        Check(calls.Count==intents.Count,"Intent did not precede every changed setter.");
        foreach(var cpu in Enumerable.Range(0,57))foreach(var gpu in Enumerable.Range(0,57))
        {
            var request=Hp8C40ProductFanTargets.BuildRequest(54) with{Payload=[(byte)cpu,(byte)gpu,0,0]};
            Check(WmiFanExperimentBoundary.IsAllowed(request,true,false,false,true,true)==Hp8C40ProductFanTargets.IsPair(cpu,gpu),"Preview admitted an arbitrary pair.");
        }
        var upper=Hp8C40ProductFanTargets.BuildRequest(54);
        foreach(var allowed in new[]{WmiFanExperimentBoundary.IsAllowed(upper,true,false,false),
            WmiFanExperimentBoundary.IsAllowed(upper,true,false,false,true),WmiFanExperimentBoundary.IsAllowed(upper,false,false,false,true,true),
            WmiFanExperimentBoundary.IsAllowed(upper,true,true,false,true,true),WmiFanExperimentBoundary.IsAllowed(upper,true,false,true,true,true)})
            Check(!allowed,"Preview bypassed lifecycle/default envelope.");
        Reject(()=>session.Apply(55,true));Reject(()=>session.Apply(9,true));Reject(()=>session.Apply(54,false));
        session.Recover();Check(calls[^2].Payload.SequenceEqual(new byte[]{255,255,0,0})&&calls[^1].CommandType==0x1A,"Preview recovery changed.");
        Reject(()=>session.Apply(54,true));
        var writes=0;var failedIntent=new WmiFanSession(_=>{writes++;return 0;},_=>throw new InvalidOperationException("intent"),10,true);
        Reject(()=>failedIntent.Apply(54,true));Check(writes==0&&!failedIntent.MayHaveWritten,"Intent failure dispatched preview hardware.");
        var rejected=new WmiFanSession(_=>7,_=>{},10,true);Reject(()=>rejected.Apply(54,true));
        Check(rejected.Phase==WmiFanSessionPhase.Recovering&&rejected.MayHaveWritten,"Rejected preview left normal writes open.");
        Console.WriteLine("Upper-range preview: PASS (54/52 mapping, exhaustive pair whitelist, default/shadow/recovery/stop refusal, intent ordering, deduplication and release; fake IO only).");
    }
}
