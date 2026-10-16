using System.Globalization;
using System.Net;
using System.Text;
using VictusFanControl.ExternalCooling;

namespace VictusFanControl.Esp32Simulator;

public sealed record SimulatorLaunchOptions(IPAddress Address,int Port,SimulatorOptions Simulation);

public static class SimulatorConsole
{
    public static bool TryOptions(string[] args,out SimulatorLaunchOptions options)
    {
        options=new(IPAddress.Loopback,8765,new());
        var seen=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<args.Length;i+=2)
        {
            if(i+1>=args.Length||!seen.Add(args[i]))return false;
            var value=args[i+1];
            switch(args[i])
            {
                case "--bind":
                    if(!IPAddress.TryParse(value,out var ip))return false;
                    options=options with{Address=ip};break;
                case "--port":
                    if(!int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var port)||port is <1 or >65535)return false;
                    options=options with{Port=port};break;
                case "--after":
                    if(!int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var after)||after<1)return false;
                    options=options with{Simulation=options.Simulation with{AfterMessages=after}};break;
                case "--fault":
                    if(!Enum.TryParse<SimulatorFault>(value,true,out var fault)||!Enum.IsDefined(fault))return false;
                    options=options with{Simulation=options.Simulation with{Fault=fault}};break;
                default:return false;
            }
        }
        return true;
    }

    // Injected key events make retries/cancellation testable without exposing keys in command arguments.
    public static byte[]? ReadPairingKey(Func<ConsoleKeyInfo> read,Action<string> write)
    {
        while(true)
        {
            write("Clave compartida Base64 (entrada oculta): ");
            var input=new StringBuilder(64);var overflow=0;
            try
            {
                while(true)
                {
                    var k=read();
                    if(k.Key==ConsoleKey.Escape||k.Key==ConsoleKey.C&&(k.Modifiers&ConsoleModifiers.Control)!=0)
                        return null;
                    if(k.Key==ConsoleKey.Enter)break;
                    if(k.Key==ConsoleKey.Backspace)
                    {if(overflow>0)overflow--;else if(input.Length>0)input.Length--;}
                    else if(!char.IsControl(k.KeyChar))
                    {if(input.Length<128&&overflow==0)input.Append(k.KeyChar);else if(overflow<int.MaxValue)overflow++;}
                }
                write(Environment.NewLine);
                if(overflow==0)
                {
                    try{return ExternalCoolingProtocol.ParsePairingKey(input.ToString());}
                    catch(Exception ex)when(ex is FormatException or ArgumentException){}
                }
                write("Clave no válida. Copia la clave completa generada por VictusFanControl (32 bytes en Base64). Escape cancela."+Environment.NewLine);
            }
            finally{input.Clear();}
        }
    }
}
