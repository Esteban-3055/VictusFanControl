namespace VictusFanControl.Telemetry;

internal static class ProductTelemetryContinuitySelfTest
{
    internal static int Run(TextWriter output)
    {
        var failures=0;
        void Check(bool ok,string message) { if(!ok) { output.WriteLine("FAIL product telemetry: "+message);failures++; } }
        var at=DateTimeOffset.UnixEpoch;
        TelemetrySnapshot Sample(int ms)=>new(at.AddMilliseconds(ms),"CPU",50,20,60,"GPU",45,10,30,2000,2000)
        {
            CpuExpectedPhysicalCoreCount=1,CpuCoreTemperatures=[new(0,0,"Performance",50)],
            FanTelemetrySource="HP-WMI-ACPI-2D",FanSampledAtUtc=at,FanAgeCapturedAtUtc=at.AddMilliseconds(ms),
            FanSampleAgeMilliseconds=ms,FanRpmResolution=100,FanMaximumAgeMilliseconds=10000
        };
        var continuity=new ProductTelemetryContinuity();
        var first=continuity.Observe(Sample(0));
        Check(first.IsComplete&&first.RetainedTelemetry is null,"initial valid observation");
        var held=continuity.Observe(Sample(3000) with{CpuTemperatureC=null,CpuCoreTemperatures=[],CpuPackagePowerW=null});
        Check(held.CpuTemperatureC==50&&held.CpuPackagePowerW==20&&held.CpuThermalSampledAtUtc==at&&held.CpuPowerSampledAtUtc==at,"retention must preserve acquisition dates");
        Check(held.HasFreshControlSensorsAt(at.AddMilliseconds(4999))&&!held.HasFreshControlSensorsAt(at.AddMilliseconds(5000)),"five-second hard boundary");
        var expired=continuity.Observe(Sample(5000) with{CpuTemperatureC=null,CpuCoreTemperatures=[],CpuPackagePowerW=null});
        Check(!expired.IsComplete&&expired.CpuTemperatureC is null,"repeated missing readings cannot renew retained values");
        Check(first.IsFanTelemetryFreshAt(at.AddMilliseconds(9999))&&!first.IsFanTelemetryFreshAt(at.AddMilliseconds(10000)),"ten-second RPM boundary");
        Check(!first.IsFanTelemetryFreshAt(at.AddMilliseconds(3000),3000),"startup RPM remains strict");
        var optional=continuity.Observe(Sample(6000) with{CpuLoadPercent=null,GpuLoadPercent=null});
        Check(optional.IsComplete&&optional.CpuLoadPercent is null&&optional.RetainedTelemetry is null,"missing utilization must remain visibly missing without blocking control");
        continuity.Reset();
        Check(!continuity.Observe(Sample(7000) with{CpuTemperatureC=null,CpuCoreTemperatures=[]}).IsComplete,"lifecycle reset cannot reuse an old thermal sample");
        var legacy=Sample(0) with{FanMaximumAgeMilliseconds=3000};
        Check(!legacy.IsFanTelemetryFreshAt(at.AddSeconds(3)),"historical fan age remains strict");
        output.WriteLine($"Product telemetry continuity: {(failures==0?"PASS":"FAIL")} (5 s critical / 10 s RPM; original dates; missing load; lifecycle reset; no hardware IO).");
        return failures;
    }
}
