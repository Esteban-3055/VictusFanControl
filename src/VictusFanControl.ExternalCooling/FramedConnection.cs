using System.Net.Sockets;
using System.Security.Cryptography;

namespace VictusFanControl.ExternalCooling;

/// <summary>Single reader / single writer; bounded UTF-8 NDJSON, whole-frame deadlines.</summary>
public sealed class FramedConnection(Stream stream) : IAsyncDisposable
{
    private readonly byte[] _buffer=new byte[1024];
    private int _position,_available;
    public async Task<ProtocolFrame> ReadAsync(CancellationToken token,TimeSpan? timeout=null)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout??TimeSpan.FromSeconds(5));
        using var line=new MemoryStream();
        while(true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if(_position==_available)
            {
                _available=await stream.ReadAsync(_buffer,deadline.Token).ConfigureAwait(false);_position=0;
                if(_available==0)throw new EndOfStreamException("Peer closed connection.");
            }
            var b=_buffer[_position++];
            if(b==10)return ExternalCoolingProtocol.Decode(line.ToArray());
            if(line.Length>=ExternalCoolingProtocol.MaximumFrameBytes)throw new InvalidDataException("Frame too large.");
            line.WriteByte(b);
        }
    }
    public async Task WriteAsync(ProtocolFrame frame,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var bytes=ExternalCoolingProtocol.Encode(frame);
        await stream.WriteAsync(bytes,deadline.Token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[]{10},deadline.Token).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()=>stream.DisposeAsync();
}

public sealed class AuthenticatedSession : IDisposable
{
    public string Id { get; }
    public byte[] SendKey { get; }
    public byte[] ReceiveKey { get; }
    private AuthenticatedSession(string id,byte[] send,byte[] receive) {Id=id;SendKey=send;ReceiveKey=receive;}
    public void Dispose() {CryptographicOperations.ZeroMemory(SendKey);CryptographicOperations.ZeroMemory(ReceiveKey);}
    private static void Handshake(ProtocolFrame frame,string type,string session)
    {if(frame.Type!=type||frame.SessionId!=session||frame.Sequence!=0||frame.Mac!="")throw new InvalidDataException("Invalid authentication exchange.");}
    public static async Task<AuthenticatedSession> ClientAsync(FramedConnection wire,byte[] key,CancellationToken token)
    {
        var id=Guid.NewGuid().ToString("N");var cn=ExternalCoolingProtocol.NewNonce();
        await wire.WriteAsync(ExternalCoolingProtocol.Create("hello",id,0,new ClientHello{ClientNonce=cn}),token).ConfigureAwait(false);
        var frame=await wire.ReadAsync(token).ConfigureAwait(false);Handshake(frame,"challenge",id);
        var challenge=ExternalCoolingProtocol.Payload<ServerChallenge>(frame);var sn=challenge.ServerNonce;
        ExternalCoolingProtocol.ValidateNonce(sn);
        if(!ExternalCoolingProtocol.EqualProof(ExternalCoolingProtocol.Proof(key,"server",id,cn,sn),challenge.Proof))throw new InvalidDataException("Authentication failed.");
        await wire.WriteAsync(ExternalCoolingProtocol.Create("authenticate",id,0,new AuthenticationProof{Proof=ExternalCoolingProtocol.Proof(key,"client",id,cn,sn)}),token).ConfigureAwait(false);
        frame=await wire.ReadAsync(token).ConfigureAwait(false);Handshake(frame,"authenticated",id);
        if(!ExternalCoolingProtocol.EqualProof(ExternalCoolingProtocol.Proof(key,"ready",id,cn,sn),ExternalCoolingProtocol.Payload<AuthenticationProof>(frame).Proof))throw new InvalidDataException("Authentication failed.");
        return new(id,ExternalCoolingProtocol.SessionKey(key,"client-to-device",id,cn,sn),ExternalCoolingProtocol.SessionKey(key,"device-to-client",id,cn,sn));
    }
    public static async Task<AuthenticatedSession> ServerAsync(FramedConnection wire,byte[] key,CancellationToken token)
    {
        var frame=await wire.ReadAsync(token).ConfigureAwait(false);var id=frame.SessionId;Handshake(frame,"hello",id);
        var cn=ExternalCoolingProtocol.Payload<ClientHello>(frame).ClientNonce;ExternalCoolingProtocol.ValidateNonce(cn);
        var sn=ExternalCoolingProtocol.NewNonce();
        await wire.WriteAsync(ExternalCoolingProtocol.Create("challenge",id,0,new ServerChallenge{ServerNonce=sn,Proof=ExternalCoolingProtocol.Proof(key,"server",id,cn,sn)}),token).ConfigureAwait(false);
        frame=await wire.ReadAsync(token).ConfigureAwait(false);Handshake(frame,"authenticate",id);
        if(!ExternalCoolingProtocol.EqualProof(ExternalCoolingProtocol.Proof(key,"client",id,cn,sn),ExternalCoolingProtocol.Payload<AuthenticationProof>(frame).Proof))throw new InvalidDataException("Authentication failed.");
        await wire.WriteAsync(ExternalCoolingProtocol.Create("authenticated",id,0,new AuthenticationProof{Proof=ExternalCoolingProtocol.Proof(key,"ready",id,cn,sn)}),token).ConfigureAwait(false);
        return new(id,ExternalCoolingProtocol.SessionKey(key,"device-to-client",id,cn,sn),ExternalCoolingProtocol.SessionKey(key,"client-to-device",id,cn,sn));
    }
}
