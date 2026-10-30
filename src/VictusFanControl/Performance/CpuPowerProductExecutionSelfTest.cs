namespace VictusFanControl.Performance;

internal static class CpuPowerProductExecutionSelfTest
{
    private const ulong BaselineRaw=0x0042839800DF8168UL;
    private const double Unit=0.125;
    private const CpuPowerExecutionPolicy Product=CpuPowerExecutionPolicy.ProductPl1ToPl2;

    internal static void Run(TextWriter output)
    {
        static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        static void Refused(Action action,string message)
        {try{action();}catch(InvalidOperationException){return;}throw new InvalidOperationException(message);}
        var baseline=CpuRaplPowerLimitCodec.DecodeSnapshot(BaselineRaw,Unit);
        var info=new CpuRaplPowerInfo(45,0,0,0);
        CpuPowerLimitApplyPlan Plan(double pl1,double pl2,CpuPowerLimitSnapshot? current=null,CpuRaplPowerInfo? reported=null)=>
            CpuRaplPowerLimitCodec.BuildPlan(baseline,current??baseline,new(pl1,pl2),Unit,reported??info,Product);
        foreach(var watts in new[]{45,60,80,114})
        {
            var plan=Plan(watts,watts);
            var snapshot=CpuRaplPowerLimitCodec.DecodeSnapshot(plan.RequestedRaw,Unit);
            Require(snapshot.Pl1Watts==watts&&snapshot.Pl2Watts==watts&&
                (plan.RequestedRaw&~CpuRaplPowerLimitCodec.OwnedMask)==(BaselineRaw&~CpuRaplPowerLimitCodec.OwnedMask),
                "Expanded CPU plan clipped PL1 or changed non-owned register bits.");
        }
        Refused(()=>CpuRaplPowerLimitCodec.BuildPlan(baseline,baseline,new(80,80),Unit,info),
            "Historical/default codec gained permission to raise PL1.");
        foreach(var request in new[]{new CpuPowerLimitRequest(81,80),new(114,115),new(115,115),new(7,8),new(double.NaN,80),new(80,double.PositiveInfinity)})
            Refused(()=>CpuRaplPowerLimitCodec.BuildPlan(baseline,baseline,request,Unit,info,Product),"Invalid product CPU request was accepted.");
        Refused(()=>Plan(80,80,baseline with{Locked=true}),"Expanded CPU plan ignored a locked register.");
        Refused(()=>Plan(80,80,reported:info with{MaximumWatts=70}),"Expanded CPU plan ignored the reported hardware maximum.");
        Refused(()=>Plan(8,15,reported:info with{MinimumWatts=10}),"Expanded CPU plan ignored the reported hardware minimum.");
        Refused(()=>CpuRaplPowerLimitCodec.BuildPlan(baseline with{Pl1Watts=40},baseline,new(80,80),Unit,info,Product),
            "Expanded policy accepted an unqualified baseline.");

        var root=Path.Combine(Path.GetTempPath(),"VFC-product-cpu-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var journal=new JsonCpuPowerSessionJournal(Path.Combine(root,"normal.json"),CpuPowerProductDefaults.TargetProfileId);
            var backend=new FixtureBackend(journal);
            using(var limiter=new CpuPowerLimiter(backend,journal,executionPolicy:Product))
            {
                Require(limiter.Apply(new(80,80))&&limiter.VerifyActive(),"Expanded CPU limiter rejected 80/80: "+limiter.LastError);
                var initial=journal.Load()!;
                Require(initial.Request==new CpuPowerLimitRequest(80,80)&&initial.OriginalBaseline==baseline,"Expanded request did not round-trip through the real journal.");
                Require(limiter.SwitchOwnedPreset(new(114,114)),"Owned switch to 114/114 failed: "+limiter.LastError);
                Require(journal.Load() is{} next&&next.SessionId==initial.SessionId&&next.OriginalBaseline==baseline,
                    "Expanded preset switch replaced the original owner/baseline.");
                backend.Raw^=1UL<<16;
                var metadata=backend.Raw&~CpuRaplPowerLimitCodec.OwnedMask;
                Require(limiter.Release()&&journal.Load() is null&&backend.Read() is{Pl1Watts:45,Pl2Watts:115}&&
                    (backend.Raw&~CpuRaplPowerLimitCodec.OwnedMask)==metadata,"Expanded CPU release did not restore owned fields and preserve current metadata.");
            }
            var historicalJournal=new JsonCpuPowerSessionJournal(Path.Combine(root,"historical.json"),CpuPowerProductDefaults.TargetProfileId);
            var historicalBackend=new FixtureBackend(historicalJournal);
            using(var historical=new CpuPowerLimiter(historicalBackend,historicalJournal))
                Require(!historical.Apply(new(80,80))&&historicalBackend.Writes==0&&historicalJournal.Load() is null,
                    "Default limiter accepted an expanded backend plan without explicit product admission.");

            var recoveryJournal=new JsonCpuPowerSessionJournal(Path.Combine(root,"recovery.json"),CpuPowerProductDefaults.TargetProfileId);
            var recoveryBackend=new FixtureBackend(recoveryJournal);
            using(var owner=new CpuPowerLimiter(recoveryBackend,recoveryJournal,executionPolicy:Product))
            {
                Require(owner.Apply(new(80,100)),"Recovery fixture could not prepare an expanded request.");
                var recovered=new CpuPowerRecoveryExecutor(recoveryBackend,recoveryJournal).Execute();
                Require(recovered.Disposition==CpuPowerRecoveryExecutionDisposition.RestoredAndCleared&&recoveryJournal.Load() is null&&
                    recoveryBackend.Read() is{Pl1Watts:45,Pl2Watts:115},"Existing release-only recovery could not restore an expanded request.");
            }
            var wrongJournal=new JsonCpuPowerSessionJournal(Path.Combine(root,"wrong.json"),"not-the-product-target");
            bool wrongRefused=false;
            try{using var invalid=new CpuPowerLimiter(new FixtureBackend(wrongJournal),wrongJournal,executionPolicy:Product);}
            catch(ArgumentException){wrongRefused=true;}
            Require(wrongRefused,"Expanded CPU policy accepted another target.");
        }
        finally{Directory.Delete(root,true);}
        output.WriteLine("PASS product CPU PL1-to-PL2 execution: 45/60/80/114 W encoding, exact-target admission, durable journal, owned switch, metadata-preserving release and release-only recovery; historical downward policy, lock and reported hardware bounds preserved; hardware IO=0.");
    }

    private sealed class FixtureBackend(ICpuPowerSessionJournal journal) : ICpuPowerLimitBackend
    {
        internal ulong Raw=BaselineRaw;
        internal int Writes;
        public bool IsSupported=>true;
        public CpuPowerLimitSnapshot Read()=>CpuRaplPowerLimitCodec.DecodeSnapshot(Raw,Unit);
        public CpuPowerLimitApplyPlan BuildApplyPlan(CpuPowerLimitSnapshot baseline,CpuPowerLimitRequest request)=>BuildOwnedTransitionPlan(baseline,request,Read());
        public CpuPowerLimitApplyPlan BuildOwnedTransitionPlan(CpuPowerLimitSnapshot baseline,CpuPowerLimitRequest request,CpuPowerLimitSnapshot current)=>
            CpuRaplPowerLimitCodec.BuildPlan(baseline,current,request,Unit,new(45,0,0,0),Product);
        public CpuPowerLimitApplyPlan BuildReacquirePlan(CpuPowerLimitSnapshot baseline,CpuPowerLimitRequest request,CpuPowerLimitSnapshot current)=>BuildOwnedTransitionPlan(baseline,request,current);
        public CpuPowerLimitRestorePlan PlanRestore(CpuPowerLimitSnapshot target,ulong applied,CpuPowerLimitSnapshot current)=>CpuRaplPowerLimitCodec.PlanRestore(target,applied,current);
        public bool OwnedFieldsMatch(ulong raw,CpuPowerLimitSnapshot current)=>CpuRaplPowerLimitCodec.OwnedFieldsMatch(raw,current.Raw);
        public void Write(ulong raw)
        {
            var record=journal.Load();
            if(record?.PendingRaw!=raw||record.Phase is not(CpuPowerJournalPhase.WriteArmed or CpuPowerJournalPhase.PresetSwitchWriteArmed or CpuPowerJournalPhase.Restoring or CpuPowerJournalPhase.ReacquireWriteArmed))
                throw new InvalidOperationException("Fixture CPU write lacks a matching durable armed journal.");
            Writes++;Raw=raw;
        }
    }
}
