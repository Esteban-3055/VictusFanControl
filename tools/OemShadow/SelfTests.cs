namespace VictusFanControl.OemShadow;

public static class SelfTests
{
    public static int Run()
    {
        int checks = 0;
        void Require(bool ok, string reason) { checks++; if (!ok) throw new InvalidOperationException(reason); }
        void Reject(Parameters p)
        { bool rejected = false; try { p.Validate(); } catch (ArgumentException) { rejected = true; } Require(rejected,"invalid parameters accepted"); }
        var start = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
        Frame F(int second, double cpu=40, double tz=42, double gpu=35, double dtt=39) =>
            new(start.AddSeconds(second),new(cpu,start.AddSeconds(second)),new(cpu,start.AddSeconds(second)),
                new(gpu,start.AddSeconds(second)),new(tz,start.AddSeconds(second)),new(dtt,start.AddSeconds(second)));
        try
        {
            foreach (var state in new[] {OemState.A,OemState.B,OemState.C,OemState.D})
            {
                var r=OemFanShadowModel.Ranges(state);
                for(int a=-1;a<=1;a++) for(int b=-1;b<=1;b++)
                {
                    Require(OemFanShadowModel.Classify(r.Cpu.Min+a,r.Gpu.Min+b)==state,"low plateau tolerance");
                    Require(OemFanShadowModel.Classify(r.Cpu.Max+a,r.Gpu.Max+b)==state,"high plateau tolerance");
                }
            }
            Require(OemFanShadowModel.Classify(null,20)==OemState.Unknown,"missing actual");
            Require(OemFanShadowModel.Classify(255,255)==OemState.Unknown,"sentinel actual");
            Require(OemFanShadowModel.Classify(31,27)==OemState.Transition,"observed ramp");
            Require(OemFanShadowModel.Classify(40,20)==OemState.Transition,"asymmetric pair is not a plateau");
            Reject(new(){ MaxSourceAgeMs=0 }); Reject(new(){ CpuUpSeconds=double.NaN });
            Reject(new(){ MaxGapMs=-1 }); Reject(new(){ MaxHistoryGapMs=1 });
            Reject(new(){ GpuUpTemp=[75,55,45] }); Reject(new(){ DownMax=[[1]] });
            Reject(new(){ GpuUpDtt3=[50,55,double.PositiveInfinity] });
            Reject(new(){ DownMax=[[55,45,50,52],[65,55,48,48],[70,65,55,52]] });
            var p = new Parameters(); var model=new OemFanShadowModel(p);
            Require(model.Evaluate(F(0)).State==OemState.Unknown,"cold start needs evidence");
            for(int i=1;i<10;i++) Require(model.Evaluate(F(i)).State==OemState.Unknown,"bootstrap dwell");
            Require(model.Evaluate(F(10)).State==OemState.Transition,"bootstrap ramp");
            for(int i=11;i<=20;i++) model.Evaluate(F(i));
            Require(model.Evaluate(F(21)).State==OemState.A,"cold bootstrap A");
            for(int i=22;i<27;i++) Require(model.Evaluate(F(i,98,97)).State==OemState.A,"brief CPU spike cannot escalate");
            Require(model.Evaluate(F(27)).State==OemState.A,"spike reset");
            for(int i=28;i<73;i++) Require(model.Evaluate(F(i,98,97)).State==OemState.A,"sustained CPU dwell");
            Require(model.Evaluate(F(73,98,97)).State==OemState.Transition,"sustained CPU escalation");
            for(int i=74;i<=83;i++) model.Evaluate(F(i,98,97));
            Require(model.Evaluate(F(84,98,97)).State==OemState.B,"CPU B");
            for(int i=85;i<133;i++) Require(model.Evaluate(F(i)).State==OemState.B,"minimum state residence");
            Require(model.Evaluate(F(133)).State==OemState.Transition,"cold descent");
            for(int i=134;i<=155;i++) model.Evaluate(F(i));
            Require(model.Evaluate(F(156,40,48)).State==OemState.A,"A tolerates warmer TZ after release");
            var lost=model.Evaluate(F(157) with {Dtt3=new()});
            Require(lost.State==OemState.Unknown && lost.CpuRange is null && lost.Confidence=="ReducedConfidence","missing DTT cannot fabricate prediction");
            Require(model.Evaluate(F(158)).State==OemState.A,"short outage retains stable history");
            Require(model.Evaluate(F(159) with {Tz01=new(42,start.AddSeconds(160))}).State==OemState.Unknown,"future sample rejected");
            Require(model.Evaluate(F(160) with {Tz01=new(42,start.AddSeconds(157))}).State==OemState.Unknown,"age boundary rejected");
            Require(model.Evaluate(F(161) with {CpuPackage=new(double.NaN,start.AddSeconds(161))}).State==OemState.Unknown,"NaN sample rejected");
            Require(model.Evaluate(F(200,85,80,65,61)).State==OemState.Unknown,"long discontinuity discards history");
            model.Reset();
            for(int i=0;i<60;i++) Require(model.Evaluate(F(i,70,60,80,68)).State==OemState.Unknown,"warm start cannot invent low state");
            Require(model.Evaluate(F(60,70,60,80,68)).State==OemState.Transition,"sustained GPU D seed");
            for(int i=61;i<=71;i++) model.Evaluate(F(i,70,60,80,68));
            Require(model.Evaluate(F(72,70,60,80,68)).State==OemState.D,"GPU D");
            for(int i=73;i<140;i++) model.Evaluate(F(i));
            Require(model.Evaluate(F(140)).State==OemState.C,"descent cannot skip multiple states");
            model.Reset();
            for(int i=0;i<45;i++) model.Evaluate(F(i,98,97));
            Require(model.Evaluate(F(45) with {Tz01=new()}).State==OemState.Unknown,"outage at threshold");
            Require(model.Evaluate(F(46,98,97)).State==OemState.Unknown,"missing time gives no hot credit");
            Require(model.Evaluate(F(45,98,97)).State==OemState.Unknown,"backward time resets");
            var firstModel=new OemFanShadowModel(); var bmodel=new OemFanShadowModel();
            for(int i=0;i<100;i++)
            {
                var f=F(i,98,97);
                Require(firstModel.Evaluate(f)==bmodel.Evaluate(f with{ActualCpuLevel=40,ActualGpuLevel=35,
                    CpuPowerW=500,GpuPowerW=500,CpuLoadPercent=100,GpuLoadPercent=100}),"prediction cannot depend on real fans or power/load");
            }
            var debounce=new ActualDebouncer(p);
            Frame AF(int second,int cpu,int gpu,int? acquired=null)=>F(second) with{
                ActualCpuLevel=cpu,ActualGpuLevel=gpu,FanSampledAtUtc=start.AddSeconds(acquired??second)};
            Require(debounce.Evaluate(AF(0,22,19)).State==OemState.Transition,"actual initial debounce");
            Require(debounce.Evaluate(AF(2,22,19,0)).State==OemState.Transition,"cached sample gives no dwell credit");
            Require(debounce.Evaluate(AF(3,22,19,0)).State==OemState.Unknown,"cached sample expires");
            for(int i=4;i<7;i++) debounce.Evaluate(AF(i,22,19));
            Require(debounce.Evaluate(AF(7,21,20)).State==OemState.A,"actual jitter shares a plateau");
            Require(debounce.Evaluate(AF(8,25,23)).State==OemState.Transition,"actual new candidate");
            debounce.Evaluate(AF(9,26,24));debounce.Evaluate(AF(10,25,23));
            var accepted=debounce.Evaluate(AF(11,26,24));
            Require(accepted.State==OemState.B && accepted.AcceptedSinceUtc==start.AddSeconds(8),"transition epoch is acquisition, not delayed acceptance");
            Require(debounce.Evaluate(AF(12,26,24,10)).State==OemState.Unknown,"regressing fan source rejected");
            var metrics=new Metrics(p);
            Prediction P(OemState state)=>new(state,null,null,Domain.Unknown,0,"TEST","TEST","TEST");
            metrics.Add(AF(0,22,19),P(OemState.A),new(OemState.A,OemState.A,start));
            metrics.Add(AF(1,22,19,0),P(OemState.Unknown),new(OemState.A,OemState.A,start));
            metrics.Add(AF(2,22,19,0),P(OemState.A),new(OemState.A,OemState.A,start));
            metrics.Add(AF(20,25,23),P(OemState.B),new(OemState.B,OemState.B,start.AddSeconds(20)));
            using var document=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(metrics.Summary("TEST"),ShadowSession.Json));
            var result=document.RootElement;
            Require(result.GetProperty("uniqueFreshFanAcquisitions").GetInt32()==2,"metrics count distinct acquisitions");
            Require(result.GetProperty("unknownPredictionSeconds").GetDouble()==1,"unknown time excludes gaps");
            Require(result.GetProperty("unobservedGapSeconds").GetDouble()==18,"gaps reported separately");
            Require(result.GetProperty("transitionMetrics").GetProperty("observed").GetArrayLength()==0,"gap cannot invent a transition");
            Console.WriteLine($"OEM shadow self-test: PASS ({checks} checks; no hardware IO)."); return 0;
        }
        catch(Exception ex){Console.Error.WriteLine($"OEM shadow self-test: FAIL after {checks} checks: {ex}");return 1;}
    }
}
