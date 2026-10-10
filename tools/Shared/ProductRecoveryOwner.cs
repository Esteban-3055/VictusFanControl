using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace VictusFanControl.Recovery;

// The only allowed live caller is the exact GUI/installer coordinating release.
// Every other Victus process still blocks recovery; names alone never grant an exception.
internal sealed record ProductRecoveryOwner(int Pid, long StartUtcTicks, string Sid)
{
    internal static ProductRecoveryOwner Capture()
    {
        using var process = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, identity.User?.Value ?? throw new IOException("Cuenta de Windows no disponible."));
    }

    internal static bool Matches(ProductRecoveryOwner expected, int pid, long ticks, string sid, string description) =>
        expected.Pid > 0 && expected.StartUtcTicks > 0 && pid == expected.Pid && ticks == expected.StartUtcTicks &&
        sid == expected.Sid && description is "VictusFanControl.App" or "VictusSetup";

    internal void Validate()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetProcessById(Pid);
        if (!OpenProcessToken(process.Handle, 8, out var token))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "No se pudo verificar la cuenta del coordinador.");
        using (token)
        using (var ownerIdentity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (identity.User?.Value != Sid || !Matches(this, process.Id, process.StartTime.ToUniversalTime().Ticks,
                ownerIdentity.User?.Value ?? "", process.MainModule?.FileVersionInfo.FileDescription ?? ""))
                throw new InvalidOperationException("El coordinador de recuperación cambió o pertenece a otra cuenta. No se autoriza la recuperación.");
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint access, out SafeAccessTokenHandle token);
}
