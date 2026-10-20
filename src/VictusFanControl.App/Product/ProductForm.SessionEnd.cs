using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private bool _waitingSessionEnd;
    private bool _sessionEndReason;
    internal void DispatchSessionEndFixture(ref Message message)
    {
        if (!_isolatedRuntime || message.Msg is not (0x11 or 0x16))
            throw new InvalidOperationException("Only isolated session-end fixtures are permitted.");
        WndProc(ref message);
    }
    private void RegisterSessionEndReason()
    {
        if (!_isolatedRuntime && IsHandleCreated)
            _sessionEndReason = ShutdownBlockReasonCreate(Handle, "Liberando controles de ventiladores y CPU/GPU.");
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
