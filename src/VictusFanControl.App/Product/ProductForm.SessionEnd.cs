using System.Diagnostics;
using System.Runtime.InteropServices;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private bool _waitingSessionEnd;
    private bool _sessionEndReason;
    private bool _sessionCoolingQuerySeen,_sessionEndConfirmed;
    private ProductShutdownCooling? _sessionCooling;
    private (Func<PerformancePowerSourceKind> Source,Func<long> Clock,Action Pulse)? _sessionCoolingFixture;
    internal void SetSessionCoolingFixture(Func<PerformancePowerSourceKind> source,Func<long> clock,Action pulse)
    {
        if(!_isolatedRuntime)throw new InvalidOperationException("Shutdown simulation requires an isolated runtime.");
        _sessionCoolingFixture=(source,clock,pulse);
    }
    internal Task SessionCoolingPreparationInFixture=>_isolatedRuntime?_sessionCooling?.Preparation??Task.CompletedTask:
        throw new InvalidOperationException("Isolated shutdown fixture required.");
    internal void DispatchSessionEndFixture(ref Message message)
    {
        if (!_isolatedRuntime || message.Msg is not (0x11 or 0x16))
            throw new InvalidOperationException("Only isolated session-end fixtures are permitted.");
        WndProc(ref message);
    }
    private bool RegisterSessionEndReason(string reason=ProductShutdownCooling.ReleaseReason)
    {
        if(_isolatedRuntime)return true;
        bool registered=IsHandleCreated&&ShutdownBlockReasonCreate(Handle,reason);
        // A failed reason update must not forget an earlier registration: it
        // still needs destroying if Windows cancels the shutdown.
        if(registered)_sessionEndReason=true;
        else AppLog.Write("PRODUCT WINDOWS SESSION END: reason registration failed; no optional delay. Win32="+Marshal.GetLastWin32Error());
        return registered;
    }
    private PerformancePowerSourceKind ReadSessionEndSource()
    {
        try
        {
            if(_isolatedRuntime)return _sessionCoolingFixture?.Source()??PerformancePowerSourceKind.Unknown;
            var reading=new WindowsPerformancePowerSourceReader().Read();
            return reading.Succeeded?reading.Source:PerformancePowerSourceKind.Unknown;
        }
        catch(Exception error){AppLog.Write("Shutdown AC query failed: "+error.GetType().Name);return PerformancePowerSourceKind.Unknown;}
    }
    private void PrepareWindowsSessionEnd(long flags)
    {
        AppLog.Write($"PRODUCT WINDOWS SESSION END QUERY: flags=0x{flags:X}; duplicate={_sessionCoolingQuerySeen}");
        const long nonOptional=0x40000000L|0x80000000L|1L; // forced, logoff, Restart Manager/update
        if((flags&nonOptional)!=0){_sessionCooling?.Cancel();RegisterSessionEndReason();return;}
        if(_sessionCoolingQuerySeen)return;
        _sessionCoolingQuerySeen=true;
        var runtime=_runtime;
        // A shutdown query is not permission to construct/recover a controller.
        // An isolated form defaults to Unknown and cannot query or write real hardware.
        if(_closing||_exitTask is not null||_restarting||_canvas.Busy||_pendingCommands!=0||_systemSuspended||
            _displayOff||!_lifecycleRelease.IsCompletedSuccessfully||runtime is null||
            !ProductShutdownCooling.Eligible(runtime.State)||ReadSessionEndSource()!=PerformancePowerSourceKind.Ac)
        {
            AppLog.Write($"PRODUCT WINDOWS SHUTDOWN COOLING: skipped; closing={_closing}; exiting={_exitTask is not null}; restarting={_restarting}; busy={_canvas.Busy}; pending={_pendingCommands}; suspended={_systemSuspended}; displayOff={_displayOff}; lifecycleReady={_lifecycleRelease.IsCompletedSuccessfully}; runtime={runtime?.State.Runtime}; authority={runtime?.State.FanAuthority}; requires eligible authority and confirmed AC.");
            RegisterSessionEndReason();return;
        }
        if(!RegisterSessionEndReason(ProductShutdownCooling.CoolingReason))return;
        _closing=true;_canvas.Busy=true;
        _startupAutomatic?.Cancel("Refrigeración previa al apagado.");_automaticResumption.Cancel();_automaticRecoveryResumeSuppressed=true;
        var cooling=new ProductShutdownCooling(runtime,ReadSessionEndSource,_sessionCoolingFixture?.Clock??(()=>Environment.TickCount64),
            ()=>ReferenceEquals(_runtime,runtime)&&_exitTask is null&&!_disposedRuntime&&!_systemSuspended);
        _sessionCooling=cooling;
        AppLog.Write("PRODUCT WINDOWS SHUTDOWN COOLING: AC confirmed at admission; source changes do not cancel the original <=20000 ms phase; nominal target=5000 RPM (50/50); no physical RPM guarantee.");
        try
        {
            cooling.Start();
            while(cooling.Continue())
            {
                _canvas.Notice=$"Refrigerando antes de apagar · {cooling.RemainingSeconds} s · solicitud nominal 5000 RPM";
                _canvas.Invalidate();Application.DoEvents();
                if(_sessionCoolingFixture is{} fixture)fixture.Pulse();else Thread.Sleep(50);
            }
        }
        catch(OperationCanceledException error){AppLog.Write("PRODUCT WINDOWS SHUTDOWN COOLING: "+error.Message);}
        catch(Exception error){AppLog.Write("PRODUCT WINDOWS SHUTDOWN COOLING: optional phase stopped: "+error.GetType().Name);}
        finally
        {
            cooling.Cancel();
            // Observe failures without detaching any native work. Final exit or
            // cancellation release below still awaits the actual preparation task.
            _=ObserveCoolingPreparationAsync(cooling);
            if(ReferenceEquals(_sessionCooling,cooling)&&!_sessionEndConfirmed)
            {RegisterSessionEndReason();_canvas.Notice="Refrigeración terminada. Esperando confirmación de Windows para liberar recursos…";}
            AppLog.Write($"PRODUCT WINDOWS SHUTDOWN COOLING: phase finished; elapsedMs={cooling.ElapsedMilliseconds}; preparation={cooling.Preparation.Status}; separate release budget remains 20000 ms.");
        }
    }
    private static async Task ObserveCoolingPreparationAsync(ProductShutdownCooling cooling)
    {
        try{await cooling.Preparation;}
        catch(Exception error){AppLog.Write("PRODUCT WINDOWS SHUTDOWN COOLING: preparation ended: "+error.GetType().Name);}
    }
    private void CancelWindowsSessionEnd()
    {
        _sessionCoolingQuerySeen=false;
        var cooling=_sessionCooling;_sessionCooling=null;
        if(cooling is null)return;
        cooling.Cancel();
        var runtime=_runtime;
        if(runtime is not null&&cooling.Attempted)
        {
            // Cancellation cannot revive the old Automatic session or leave the
            // temporary 50/50 target indefinitely. Keep the ordinary lifecycle fence.
            try{runtime.FenceLifecycle("Apagado cancelado tras refrigeración; volver a Firmware.");}
            catch(Exception error){_powerFenceFailure=true;AppLog.Write("Shutdown cancellation fence unresolved: "+error);}
            var previous=_lifecycleRelease;
            _lifecycleRelease=ReleaseCancelledCoolingAsync(runtime,cooling,previous);
        }
        if(_exitTask is null)
        {
            _closing=false;_canvas.Busy=false;
            _canvas.Notice=cooling.Attempted?"Apagado cancelado. Volviendo a Firmware; Automático requiere un nuevo inicio explícito.":
                "Apagado cancelado. La refrigeración no llegó a iniciarse.";
            _canvas.Invalidate();
        }
    }
    private static async Task ReleaseCancelledCoolingAsync(IProductRuntime runtime,ProductShutdownCooling cooling,Task previous)
    {
        await ObserveCoolingPreparationAsync(cooling);
        // A prior failed release remains evidence, but cannot skip this release.
        try{await previous;}catch(Exception error){AppLog.Write("Earlier cooling release unresolved: "+error);}
        try{await runtime.ReleaseForLifecycleAsync("Apagado cancelado; retirar refrigeración temporal.");}
        catch(Exception error){AppLog.Write("Cancelled shutdown cooling release unresolved: "+error);throw;}
    }
    private void ClearSessionEndReason()
    {
        if (_sessionEndReason && IsHandleCreated) ShutdownBlockReasonDestroy(Handle);
        _sessionEndReason = false;
    }
    internal bool WaitForWindowsSessionEnd(TimeSpan budget)
    {
        if (_waitingSessionEnd) return false;
        _waitingSessionEnd = true;
        try
        {
            var exit = BeginExitAsync(closeWindow: false);
            var elapsed = Stopwatch.StartNew();
            // Disposal and pending UI commands can post to this STA. Pump those
            // continuations while _closing fences new commands/power/startup.
            // A deadline never cancels a native mutator or deletes its journal.
            while (!exit.IsCompleted && elapsed.Elapsed < budget)
            {
                Application.DoEvents();
                if (!exit.IsCompleted) Thread.Sleep(10);
            }
            if (!exit.IsCompleted)
                AppLog.Write("PRODUCT WINDOWS SESSION END: cleanup deadline; pending journals retained; no release inferred.");
            else
            {
                AppLog.Write($"PRODUCT WINDOWS SESSION END RESULT: exitTask={exit.Status}; exitCode={Environment.ExitCode}; elapsedMs={elapsed.ElapsedMilliseconds}; physical firmware ownership not inferred.");
                // Keep the final acknowledgement available after reboot. Optional
                // log flushing shares the existing release deadline; it cannot
                // extend that deadline or turn a failed release into success.
                var remaining=budget-elapsed.Elapsed;
                if(remaining>TimeSpan.Zero)
                {
                    var flush=AppLog.FlushAsync(remaining<TimeSpan.FromSeconds(1)?remaining:TimeSpan.FromSeconds(1));
                    while(!flush.IsCompleted&&elapsed.Elapsed<budget)
                    {Application.DoEvents();if(!flush.IsCompleted)Thread.Sleep(10);}
                }
            }
            return exit.IsCompleted;
        }
        finally { _waitingSessionEnd = false; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
}
