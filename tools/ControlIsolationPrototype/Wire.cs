using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VfcIsolationFixture;

internal sealed record Challenge(int Version,string Engine,string Nonce);
internal sealed record Request(int Version,string Client,long Sequence,string Operation,string Mac);
internal sealed record EngineState(string Engine,int OwnerPid,long OwnerStart,long Generation,bool Pending,
    bool Authenticated=false,bool CommandAllowed=false,bool FixtureOnly=true,bool HardwareAuthority=false);
internal sealed record SignedState(string Body,string Mac);

internal static class Wire
{
    internal static string Mac(byte[] key,string data)=>Convert.ToHexString(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(data)));
    internal static bool Valid(byte[] key,string data,string? mac)
    {
        if(mac?.Length!=64)return false;
        try{return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(mac),Convert.FromHexString(Mac(key,data)));}
        catch(FormatException){return false;}
    }
    internal static string RequestBody(Challenge c,Request r)=>$"{r.Version}\n{c.Engine}\n{c.Nonce}\n{r.Client}\n{r.Sequence}\n{r.Operation}";
    internal static async Task<string> ReadAsync(TextReader reader,CancellationToken token)
    {
        var text=new StringBuilder();var character=new char[1];
        while(await reader.ReadAsync(character.AsMemory(),token)!=0)
        {
            if(character[0]=='\n')return text.ToString().TrimEnd('\r');
            if(text.Length>=4096)throw new InvalidDataException("Fixture frame limit.");
            text.Append(character[0]);
        }
        throw new EndOfStreamException();
    }
    internal static Task WriteAsync<T>(TextWriter writer,T value,CancellationToken token)=>writer.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(),token);
    internal static async Task<EngineState> ViewAsync(string pipe,byte[] key,string operation="state",bool invalidMac=false,
        Request? replay=null,Action<Request>? capture=null)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await stream.ConnectAsync(timeout.Token);
        using var reader=new StreamReader(stream,new UTF8Encoding(false),leaveOpen:true);
        using var writer=new StreamWriter(stream,new UTF8Encoding(false),leaveOpen:true){AutoFlush=true};
        var challenge=JsonSerializer.Deserialize<Challenge>(await ReadAsync(reader,timeout.Token))??throw new InvalidDataException();
        if(challenge.Version!=1||!Guid.TryParseExact(challenge.Engine,"N",out _))throw new InvalidDataException();
        var request=new Request(1,Guid.NewGuid().ToString("N"),1,operation,"");
        request=request with{Mac=invalidMac?new string('0',64):Mac(key,RequestBody(challenge,request))};
        if(replay is not null)request=replay;
        capture?.Invoke(request);
        await WriteAsync(writer,request,timeout.Token);
        var reply=JsonSerializer.Deserialize<SignedState>(await ReadAsync(reader,timeout.Token))??throw new InvalidDataException();
        if(!Valid(key,challenge.Nonce+"\n"+reply.Body,reply.Mac))throw new InvalidDataException("Fixture response authentication failed.");
        var state=JsonSerializer.Deserialize<EngineState>(reply.Body)??throw new InvalidDataException();
        if(state.Engine!=challenge.Engine||!state.FixtureOnly||state.HardwareAuthority)throw new InvalidDataException("Invalid fixture identity.");
        return state;
    }
}
