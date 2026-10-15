using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VictusFanControl.ExternalCooling;
using VictusFanControl.Esp32Simulator;

if(args.Contains("--help"))
{
    Console.WriteLine("ESP32 simulator: --port 8765 --bind 127.0.0.1 --fault None|Disconnect|Invalid|Partial|Delayed|Duplicate|Silence|Fragmented --after 3\nEnter the shared Base64 pairing key at the hidden prompt. Ctrl+C exits. No real hardware IO.");return;
}
string Option(string name,string fallback)=>Array.IndexOf(args,name) is var i&&i>=0&&i+1<args.Length?args[i+1]:fallback;
var port=int.Parse(Option("--port","8765"));var address=IPAddress.Parse(Option("--bind","127.0.0.1"));
var options=new SimulatorOptions(Enum.Parse<SimulatorFault>(Option("--fault","None"),true),int.Parse(Option("--after","3")));
if(port is <1 or >65535||options.AfterMessages<1)throw new ArgumentException("Invalid port/after.");
Console.Write("Clave compartida Base64 (entrada oculta): ");
var input=new System.Text.StringBuilder();
while(true)
{
    var k=Console.ReadKey(true);if(k.Key==ConsoleKey.Enter)break;
    if(k.Key==ConsoleKey.Backspace){if(input.Length>0)input.Length--;}
    else if(!char.IsControl(k.KeyChar))input.Append(k.KeyChar);
}
var key=ExternalCoolingProtocol.ParsePairingKey(input.ToString());input.Clear();Console.WriteLine();
using var cancel=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;cancel.Cancel();};
var listener=new TcpListener(address,port);listener.Start(1);
Console.WriteLine($"Simulador TCP {address}:{port}, fallo={options.Fault}. Ctrl+C para salir.");
try
{
    while(!cancel.IsCancellationRequested)
    {
        using var tcp=await listener.AcceptTcpClientAsync(cancel.Token);tcp.NoDelay=true;
        try{await Simulator.HandleAsync(tcp,key,options,(session,t)=>Console.WriteLine($"{DateTimeOffset.Now:T} sesión={session[..8]} CPU={t.CpuUsagePct} GPU={t.GpuUsagePct} RAM={t.RamUsagePct} Demanda={t.DemandaPct} Solicitud={t.Solicitud} válida={t.SolicitudValida} {t.ModoEfectivo}"),cancel.Token);}
        catch(Exception ex)when(ex is IOException or System.Text.Json.JsonException or FormatException or OperationCanceledException){Console.WriteLine("Conexión terminada o mensaje rechazado.");}
    }
}
catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
finally{listener.Stop();CryptographicOperations.ZeroMemory(key);}
