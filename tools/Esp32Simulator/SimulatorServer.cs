using System.Net.Sockets;
using System.Text.Json;
using VictusFanControl.ExternalCooling;

namespace VictusFanControl.Esp32Simulator;

/// <summary>The production accept loop, also exercised by integration tests.</summary>
public static class SimulatorServer
{
    public static async Task RunAsync(TcpListener listener,byte[] key,SimulatorOptions options,
        Action<string,ExportTelemetry>? observed,Action<string>? failure,CancellationToken token,
        SimulatorDeviceState? device=null)
    {
        try
        {
            while(!token.IsCancellationRequested)
            {
                using var tcp=await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                tcp.NoDelay=true;
                try{await Simulator.HandleAsync(tcp,key,options,observed,token,device).ConfigureAwait(false);}
                catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
                catch(Exception ex)when(ex is IOException or InvalidDataException or SocketException or JsonException or FormatException or OperationCanceledException)
                {
                    // Fixed text only: never log peer input, authentication proofs or exception contents.
                    failure?.Invoke("Conexión terminada o mensaje rechazado.");
                }
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(SocketException)when(token.IsCancellationRequested){}
        catch(ObjectDisposedException)when(token.IsCancellationRequested){}
    }
}
