using System.Diagnostics;
using VictusFanControl.ExternalCooling;

if(args.Length!=3)throw new ArgumentException("Expected Python executable, TCP bridge path, C++ firmware host path.");
var start=new ProcessStartInfo(args[0]) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
start.ArgumentList.Add(args[1]);start.ArgumentList.Add(args[2]);
using var bridge=Process.Start(start)??throw new IOException("Bridge did not start.");
try
{
    var line=await bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if(!int.TryParse(line,out var port))throw new InvalidDataException("Bridge did not publish its loopback port.");
    var client=new Esp32TcpClient();
    using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var source=new Source();
    var connection=client.RunAsync(new() {Host="127.0.0.1",Port=port,Mode=ExternalCoolingMode.MonitorAndRequest,AutoReconnect=false},
        Convert.FromBase64String("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="),source,cancellation.Token);
    try
    {
        while(client.Diagnostics.Received<4&&!connection.IsCompleted)await Task.Delay(50,cancellation.Token);
        var d=client.Diagnostics;
        if(d.State!=ExternalCoolingConnectionState.Connected || d.Failures!=0 || d.Received<4 ||
            d.Device is not {Mode:"Remote",ExternalCurveVersion:1,AppliedConfigRevision:not null,AppliedDuty:>=30 and <=255,ExternalRequest:>=90 and <=100} || d.Device.Extensions is null||
            d.Device.AppliedConfigRevision!=new ExternalCurveConfiguration().Revision)
            throw new InvalidOperationException($"Firmware interoperability failed: state={d.State}, failures={d.Failures}, received={d.Received}, device={d.Device}");
        Console.WriteLine("PASS production Windows TCP client <-> actual C++ firmware engine: mutual authentication, directional MACs, fragmented replies, Battery telemetry, remote request mapping and reserved extensions; no hardware IO.");
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
