using System.Security.Cryptography;

namespace VictusFanControl.ExternalCooling;

/// <summary>Serializes configuration, suspend/resume and shutdown. Owns one network worker.</summary>
public sealed class ExternalCoolingManager(ITelemetryExportSource source,Func<byte[]?> readPairingKey) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly object _sync=new();
    private readonly Esp32TcpClient _client=new();
    private CancellationTokenSource? _cancellation;
    private Task? _run;
    private ExternalCoolingSettings _settings=new();
    private bool _suspended,_disposed,_desired;
    public ExternalCoolingDiagnostics Diagnostics=>_client.Diagnostics;
    public async Task ConfigureAsync(ExternalCoolingSettings settings,bool connect=true)
    {
        settings.Validate();await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if(_disposed)return;
            await StopAsync().ConfigureAwait(false);
            _settings=settings;_desired=connect&&settings.Mode!=ExternalCoolingMode.Disabled;
            Start();
        }
        finally{_gate.Release();}
    }
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try{_desired=false;await StopAsync().ConfigureAwait(false);_client.SetIdle(_settings.Mode==ExternalCoolingMode.Disabled);}
        finally{_gate.Release();}
    }
    public void Suspend()
    {
        // Cancellation only, safe for WM_POWERBROADCAST: never wait for DNS/socket/worker here.
        lock(_sync){_suspended=true;_cancellation?.Cancel();_client.SetIdle(_settings.Mode==ExternalCoolingMode.Disabled);}
    }
    public async Task ResumeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try{if(_disposed)return;await StopAsync().ConfigureAwait(false);lock(_sync)_suspended=false;Start();}
        finally{_gate.Release();}
    }
    private void Start()
    {
        lock(_sync)
        {
            _client.SetIdle(_settings.Mode==ExternalCoolingMode.Disabled);
            if(_disposed||_suspended||!_desired)return;
            byte[]? key=null;
            try
            {
                key=readPairingKey();
                if(key is not{Length:32})throw new InvalidOperationException("Configura el emparejamiento antes de conectar.");
                _cancellation=new();var token=_cancellation.Token;var settings=_settings;var ownedKey=key;
                _run=Task.Run(()=>_client.RunAsync(settings,ownedKey,source,token));key=null;
            }
            finally{if(key is not null)CryptographicOperations.ZeroMemory(key);}
        }
    }
    private async Task StopAsync()
    {
        lock(_sync)_cancellation?.Cancel();
        if(_run is not null)await _run.ConfigureAwait(false);
        lock(_sync){_cancellation?.Dispose();_cancellation=null;_run=null;}
    }
    public async ValueTask DisposeAsync()
    {
        Suspend();await _gate.WaitAsync().ConfigureAwait(false);
        try{_disposed=true;_desired=false;await StopAsync().ConfigureAwait(false);_client.SetIdle(true);}
        finally{_gate.Release();}
    }
}
