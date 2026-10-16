using System.Net.Sockets;
using System.Security.Cryptography;
using VictusFanControl.Esp32Simulator;

if(args is ["--help"])
{
    Console.WriteLine("ESP32 simulator: --port 8765 --bind 127.0.0.1 --fault None|Disconnect|Invalid|Partial|Delayed|Duplicate|Silence|Fragmented --after 3\nIntroduce la clave Base64 en la entrada oculta; Escape cancela. Ctrl+C sale del servidor. Sin operaciones de hardware.");
    return 0;
}
if(!SimulatorConsole.TryOptions(args,out var options))
{Console.Error.WriteLine("Configuración no válida. Usa --help; puerto 1–65535 y --after positivo.");return 2;}
if(Console.IsInputRedirected)
{Console.Error.WriteLine("Abre el simulador en una terminal interactiva para introducir la clave oculta. No se aceptan claves por argumentos.");return 2;}
byte[]? key;
var previous=Console.TreatControlCAsInput;
try
{
    Console.TreatControlCAsInput=true;
    key=SimulatorConsole.ReadPairingKey(()=>Console.ReadKey(true),Console.Write);
}
finally{Console.TreatControlCAsInput=previous;}
if(key is null){Console.WriteLine("Cancelado.");return 0;}
using var cancel=new CancellationTokenSource();
ConsoleCancelEventHandler stop=(_,e)=>{e.Cancel=true;cancel.Cancel();};
Console.CancelKeyPress+=stop;
var listener=new TcpListener(options.Address,options.Port);
try
{
    listener.Start(1);
    Console.WriteLine($"Simulador TCP {options.Address}:{options.Port}, fallo={options.Simulation.Fault}. Ctrl+C para salir.");
    await SimulatorServer.RunAsync(listener,key,options.Simulation,
        (session,t)=>Console.WriteLine($"{DateTimeOffset.Now:T} sesión={session[..8]} CPU={t.CpuUsagePct} GPU={t.GpuUsagePct} RAM={t.RamUsagePct} Demanda={t.DemandaPct} Solicitud={t.Solicitud} válida={t.SolicitudValida} {t.ModoEfectivo}"),
        Console.WriteLine,cancel.Token);
    return 0;
}
catch(SocketException)
{Console.Error.WriteLine("No se pudo abrir el servidor. Revisa que la dirección sea local y el puerto esté disponible.");return 3;}
finally
{
    listener.Stop();CryptographicOperations.ZeroMemory(key);Console.CancelKeyPress-=stop;
}
