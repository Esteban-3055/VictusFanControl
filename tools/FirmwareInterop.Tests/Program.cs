using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.ExternalCooling;

if(args.Length!=3)throw new ArgumentException("Expected Python executable, TCP bridge path, C++ firmware host path.");
var defaults=new ExternalCurveConfiguration();
var varied=defaults with
{
    Revision="1234567890abcdef1234567890abcdef",Points=[new(0,0),new(37,91),new(100,241)],
    Input=1,RequestMinimum=7,RequestMaximum=47,ManualOutput=87,
    Rise=17,Fall=23,FilterMs=2345,Hysteresis=7,DownHoldMs=4567,StartMs=1234,StartOutput=201
};
var disabled=defaults with
{
    Revision="2234567890abcdef1234567890abcdef",Points=[new(0,0),new(100,255)],
    RequestMaximum=65535,ManualOutput=0,Rise=1,Fall=100,FilterMs=0,Hysteresis=0,DownHoldMs=0,StartMs=0,StartOutput=0
};
var maximum=defaults with
{
    Revision="3234567890abcdef1234567890abcdef",Points=[new(0,255),new(100,255)],
    ManualOutput=255,Rise=100,Fall=1,FilterMs=10000,Hysteresis=30,DownHoldMs=30000,StartMs=3000,StartOutput=255
};
var smooth=varied.WithMaximumSmoothing() with{Revision="4234567890abcdef1234567890abcdef"};
foreach(var curve in new[]{defaults,varied,disabled,maximum,smooth})await RunCase(curve);

async Task RunCase(ExternalCurveConfiguration curve)
{
curve.Validate();
var report=Path.Combine(Path.GetTempPath(),$"vfc-firmware-config-{Guid.NewGuid():N}.jsonl");
var start=new ProcessStartInfo(args[0]) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
start.ArgumentList.Add(args[1]);start.ArgumentList.Add(args[2]);
start.ArgumentList.Add("--configuration-report");start.ArgumentList.Add(report);
using var bridge=Process.Start(start)??throw new IOException("Bridge did not start.");
try
{
    var line=await bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if(!int.TryParse(line,out var port))throw new InvalidDataException("Bridge did not publish its loopback port.");
    var client=new Esp32TcpClient();
    using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var source=new Source();
    var connection=client.RunAsync(new() {Host="127.0.0.1",Port=port,Mode=ExternalCoolingMode.MonitorAndRequest,AutoReconnect=false,Curve=curve},
        Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="),source,cancellation.Token);
    try
    {
        while(client.Diagnostics.Received<4&&!connection.IsCompleted)await Task.Delay(50,cancellation.Token);
        var d=client.Diagnostics;
        if(d.State!=ExternalCoolingConnectionState.Connected || d.Failures!=0 || d.Received<4 ||
            d.Device is not {Mode:"Remote",ExternalCurveVersion:1,AppliedConfigRevision:not null,AppliedDuty:>=0 and <=255} || d.Device.Extensions is null||
            d.Device.AppliedConfigRevision!=curve.Revision)
            throw new InvalidOperationException($"Firmware interoperability failed: state={d.State}, failures={d.Failures}, received={d.Received}, device={d.Device}");
        if(curve==defaults && d.Device.ExternalRequest is not(>=90 and <=100))
            throw new InvalidOperationException("Default demand-to-request mapping changed.");
        // The report comes from the actual C++ parser after configure(); the signed ACK
        // independently reports the applied revision on the following control tick.
        using(var stream=new FileStream(report,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
        using(var reader=new StreamReader(stream))
        {
            var content=await reader.ReadToEndAsync();
            // A later telemetry frame may be writing while we inspect an earlier ACK.
            var records=content[..(content.LastIndexOf('\n')+1)].Split('\n',StringSplitOptions.RemoveEmptyEntries);
            if(records.Length==0)throw new InvalidDataException("C++ did not record a configuration.");
            foreach(var record in records)
            {
                var received=JsonSerializer.Deserialize<ExternalCurveConfiguration>(record,new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if(received!=curve)throw new InvalidOperationException($"A configuration field changed between Windows and C++: {record}");
            }
        }
        Console.WriteLine($"PASS production Windows TCP client <-> actual C++ firmware: all configuration fields match, applied revision {curve.Revision}; mutual authentication, directional MACs, fragmented replies, Battery telemetry; no hardware IO.");
        var before=d.Received;source.Firmware=true;
        while(client.Diagnostics.Received<before+2&&!connection.IsCompleted)await Task.Delay(50,cancellation.Token);
        if(client.Diagnostics.Failures!=0 || client.Diagnostics.Device is not {Mode:"Local"} || client.Diagnostics.LastSent?.Contains("\"solicitud\":null")!=true)
            throw new InvalidOperationException("Firmware/null request did not restore Local in the C++ device engine.");
        Console.WriteLine("PASS actual Windows Firmware transition invalidates Solicitud and restores device Local.");
    }
    finally {cancellation.Cancel();await connection;}
}
finally
{
    if(!bridge.WaitForExit(3000)){bridge.Kill(entireProcessTree:true);bridge.WaitForExit(1000);}
    File.Delete(report);
}
}

sealed class Source : ITelemetryExportSource
{
    public volatile bool Firmware;
    public ExportTelemetry Capture(DateTimeOffset now)
    {
        var firmware=Firmware;
        return new()
        {
        CapturedAtUtc=now,SampledAtUtc=now,DecisionAtUtc=now,CpuTempSampledAtUtc=now,GpuTempSampledAtUtc=now,RamSampledAtUtc=now,
        CpuUsagePct=10,GpuUsagePct=0,RamUsagePct=44,DemandaPct=firmware?null:35,Solicitud=firmware?null:27,SolicitudValida=!firmware,CpuTargetLevel=firmware?null:27,GpuTargetLevel=firmware?null:27,
        ModoSolicitado=firmware?"Firmware":"Automatic",ModoEfectivo=firmware?"Firmware":"Automatic",CpuTempC=55,GpuTempC=42,PowerSource="Battery",RequestEvidence=firmware?"Unavailable":"WmiAcceptedSupervised"
        };
    }
}
