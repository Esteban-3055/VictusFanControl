using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;

namespace VictusFanControl.Hardware.Hp;

internal sealed record CtgpCommand(Guid Id, int OwnerPid, long OwnerStartUtcTicks, bool Enabled);
internal sealed record CtgpStatus(string State, bool Active, bool Managed, string? Failure, DateTimeOffset Utc)
{
    internal static CtgpStatus Disabled => new("Disabled", false, false, null, DateTimeOffset.UtcNow);
}

/// <summary>Fixed, qualified 8C40 policy only. Never a watts or arbitrary WMI interface.</summary>
internal sealed class Hp8C40CtgpControl
{
    private readonly Func<HpBiosRequest, HpBiosResponse> _send;
    private readonly Func<bool> _ac;
    private readonly Action _qualify;
    private readonly Action _persistIntent;
    private bool _qualified, _owned, _externallyLost;
    internal CtgpStatus Status { get; private set; } = CtgpStatus.Disabled;
    internal bool NeedsRestore => _owned;
    internal Hp8C40CtgpControl(Func<HpBiosRequest, HpBiosResponse> send, Func<bool> ac,
        Action qualify, Action persistIntent) => (_send, _ac, _qualify, _persistIntent) = (send, ac, qualify, persistIntent);

    internal void Apply(bool enabled)
    {
        if (!enabled || !_ac()) { Restore(); SetStatus(enabled ? "AcOnly" : "Disabled", false); return; }
        if (_externallyLost) throw new InvalidOperationException("cTGP cambió fuera de esta sesión; no se vuelve a activar.");
        if (_owned) { Observe(); return; }
        if (!_qualified)
        {
            _qualify();
            var adapter = Send(0x28, [0, 0, 0, 0], 128);
            if (adapter.Length < 2 || adapter[0] + 256 * adapter[1] != 200)
                throw new InvalidOperationException("cTGP requiere el cargador HP de 200 W validado.");
            _qualified = true;
        }
        if (!Read().SequenceEqual(new byte[] { 0, 1, 1, 0 }))
            throw new InvalidOperationException("La política HP inicial difiere de la calificada; cTGP no se modifica.");
        _persistIntent(); // Durable before the setter; a failure prevents dispatch.
        _owned = true; // A rejected/throwing native call may still have changed policy.
        if (!_ac()) throw new InvalidOperationException("La fuente cambió antes de activar cTGP.");
        Send(0x22, [1, 1, 1, 0], 4);
        if (!Read().SequenceEqual(new byte[] { 1, 1, 1, 0 }))
            throw new InvalidOperationException("HP no confirmó la activación de cTGP.");
        SetStatus("Active", true);
    }

    internal void Observe()
    {
        if (!_owned) { Status = Status with { Utc = DateTimeOffset.UtcNow }; return; }
        if (!_ac()) { Restore(); SetStatus("AcOnly", false); return; }
        if (!Read().SequenceEqual(new byte[] { 1, 1, 1, 0 }))
        {
            _externallyLost = true;
            throw new InvalidOperationException("Otra política cambió cTGP; se libera sin reactivarlo.");
        }
        SetStatus("Active", true);
    }

    internal void Restore()
    {
        if (!_owned) return;
        Send(0x22, [0, 1, 1, 0], 4);
        if (!Read().SequenceEqual(new byte[] { 0, 1, 1, 0 }) ||
            !Read().SequenceEqual(new byte[] { 0, 1, 1, 0 }))
            throw new InvalidOperationException("Restauración cTGP sin confirmar; conservar la sesión pendiente.");
        _owned = false;
        SetStatus("Restored", false);
    }

    internal void Fail(Exception error) => Status = new("RecoveryRequired", false, _owned, error.Message, DateTimeOffset.UtcNow);
    internal static void RestoreRecorded(Func<HpBiosRequest, HpBiosResponse> send)
    {
        var recovery = new Hp8C40CtgpControl(send, () => true, () => { }, () => { }) { _owned = true };
        recovery.Restore();
    }
    private void SetStatus(string state, bool active) => Status = new(state, active, _owned, null, DateTimeOffset.UtcNow);
    private byte[] Read() => Send(0x21, [0, 0, 0, 0], 4);
    private byte[] Send(uint type, byte[] data, int output)
    {
        var reply = _send(new(0x20008, type, data, output));
        if (reply.ReturnCode != 0 || reply.Data.Length < output)
            throw new HpBiosCallException($"HP cTGP: respuesta inválida (tipo {type:X}, rc={reply.ReturnCode}).");
        return reply.Data;
    }
}

/// <summary>Synchronous guardian-only capability. It cannot authorize fan or arbitrary HP requests.</summary>
internal sealed class Hp8C40CtgpRequestScope : IDisposable
{
    [ThreadStatic] private static Hp8C40CtgpRequestScope? _current;
    private readonly Hp8C40CtgpRequestScope? _previous;
    private readonly Action _admitEnable;
    internal Hp8C40CtgpRequestScope(Action admitEnable) { _previous = _current; _current = this; _admitEnable = admitEnable; }
    internal static bool Qualified(HpBiosRequest r) => r.Command == 0x20008 && r.Payload.Length == 4 &&
        ((r.CommandType == 0x21 && r.OutputSize == 4 || r.CommandType == 0x28 && r.OutputSize == 128) && r.Payload.All(b => b == 0) ||
         r.CommandType == 0x22 && r.OutputSize == 4 &&
         (r.Payload.SequenceEqual(new byte[] { 1, 1, 1, 0 }) || r.Payload.SequenceEqual(new byte[] { 0, 1, 1, 0 })));
    internal static bool Admit(HpBiosRequest r)
    {
        if (_current is null || !Qualified(r)) return false;
        if (r.CommandType == 0x22 && r.Payload[0] == 1) _current._admitEnable();
        return true;
    }
    internal static void BeforeNative(HpBiosRequest r)
    {
        if (r.Command == 0x20008 && r.CommandType is 0x21 or 0x22 or 0x28 && !Admit(r))
            throw new InvalidOperationException("cTGP sin autorización del guardián WMI.");
    }
    public void Dispose() => _current = _previous;
}

internal static class Hp8C40CtgpQualification
{
    internal static void Verify()
    {
        if (HpHardwareTargetResolver.Resolve(HardwareIdentityReader.ReadCurrent(), out _) != Hp8C40TargetProfile.Instance)
            throw new InvalidOperationException("cTGP no calificado para este equipo/BIOS.");
        var size = Native.GetSystemFirmwareTable(0x41435049, 0x54445344, null, 0);
        if (size != 678970) throw new InvalidOperationException("DSDT cTGP no calificada.");
        var bytes = new byte[size];
        if (Native.GetSystemFirmwareTable(0x41435049, 0x54445344, bytes, size) != size ||
            Convert.ToHexString(SHA256.HashData(bytes)) != "E4AB0DA54F9E18B50F68D6F38ECA5E7B95DB04FCC9435B8B8E732871FCB8ED39")
            throw new InvalidOperationException("Firmware distinto del auditado para cTGP.");
        using var query = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_VideoController");
        query.Options.Timeout = TimeSpan.FromSeconds(3);
        using var results = query.Get();
        var matched = false;
        foreach (ManagementObject gpu in results)
        { using (gpu) { matched |= gpu["PNPDeviceID"]?.ToString()?.Contains("VEN_10DE&DEV_28A0&SUBSYS_8C40103C", StringComparison.OrdinalIgnoreCase) == true; } }
        if (!matched) throw new InvalidOperationException("GPU/subsystem no calificado para cTGP.");
        if (Native.nvmlInit_v2() != 0) throw new InvalidOperationException("NVML no disponible para calificar cTGP.");
        try
        {
            var driver = new StringBuilder(96); var bios = new StringBuilder(96);
            if (Native.nvmlSystemGetDriverVersion(driver, 96) != 0 || driver.ToString() != "621.01" ||
                Native.nvmlDeviceGetHandleByIndex_v2(0, out var device) != 0 ||
                Native.nvmlDeviceGetVbiosVersion(device, bios, 96) != 0 || !bios.ToString().Equals("95.07.2B.00.54", StringComparison.OrdinalIgnoreCase) ||
                Native.nvmlDeviceGetPowerManagementLimitConstraints(device, out _, out var max) != 0 || max != 75000)
                throw new InvalidOperationException("Driver, VBIOS o máximo de potencia fuera de la calificación cTGP.");
        }
        finally { Native.nvmlShutdown(); }
    }
    private static class Native
    {
        [DllImport("kernel32.dll")] internal static extern uint GetSystemFirmwareTable(uint provider, uint table, byte[]? data, uint size);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int nvmlInit_v2();
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int nvmlShutdown();
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int nvmlSystemGetDriverVersion(StringBuilder value, uint length);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int nvmlDeviceGetVbiosVersion(IntPtr device, StringBuilder value, uint length);
        [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)] internal static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint min, out uint max);
    }
}
