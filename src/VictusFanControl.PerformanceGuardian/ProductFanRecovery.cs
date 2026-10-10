using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Recovery;
using VictusFanControl.Runtime;
using VictusFanControl.Watchdog;

namespace VictusFanControl.PerformanceGuardian;

/// <summary>Explicit recovery only. No normal fan target, watchdog service or Automatic activation is admitted.</summary>
internal static class ProductFanRecovery
{
    internal sealed record Options(string Request, string Hash, string Modules, string Output, ProductRecoveryOwner Owner, int Index);
    internal static Options Parse(string[] a)
    {
        if (a.Length != 18 || a[0] != "--recover-product-fans" || a[1] != "--request" || a[3] != "--request-sha" ||
            a[5] != "--modules" || a[7] != "--output" || a[9] != "--owner-pid" || a[11] != "--owner-start" || a[13] != "--owner-sid" ||
            a[15] != "--confirm-release-only" || a[16] != "--record-index" || !int.TryParse(a[10], out var pid) || pid <= 0 ||
            !long.TryParse(a[12], out var ticks) || ticks <= 0 || !int.TryParse(a[17], out var index) || index < 0 ||
            a[4].Length != 64 || string.IsNullOrWhiteSpace(a[14])) throw new ArgumentException("Identidad o autorización de recuperación de ventiladores inválida.");
        return new(Path.GetFullPath(a[2]), a[4], Path.GetFullPath(a[6]), Path.GetFullPath(a[8]), new(pid, ticks, a[14]), index);
    }
    internal static int Run(string[] args)
    {
        var o = Parse(args); var bytes = ProductRecoveryInventory.ReadBytes(o.Request);
        if (ProductRecoveryInventory.Hash(bytes) != o.Hash) throw new IOException("La solicitud de recuperación cambió.");
        var selected = JsonSerializer.Deserialize<ProductRecoveryRecord[]>(bytes) ?? throw new IOException("Solicitud vacía.");
        if (o.Index >= selected.Length || selected.Length is 0 or > 100) throw new IOException("Selección inválida.");
        ProductRecoveryInventory.EnsurePlainPath(Path.Combine(o.Output, "check"));
        if (ProductRecoveryInventory.Exists(o.Output)) throw new IOException("La evidencia debe usar una carpeta nueva.");
        Directory.CreateDirectory(o.Output);
        string? failure = null; object? result = null; var succeeded = false;
        using var mutex = new Mutex(false, @"Global\VictusFanControl.WmiFanExperiment.Native");
        var owns = false;
        try
        {
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; throw new IOException("La llamada WMI anterior quedó incierta. Usa Reiniciar Windows antes de recuperar; apagar con Inicio rápido no basta."); }
            if (!owns) throw new IOException("Otra llamada WMI sigue activa. No se envió una recuperación.");
            RequireIsolation(o.Owner);
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new IOException("La recuperación requiere administrador con la misma cuenta.");
            if (!Hp8C40TargetProfile.Matches(HardwareIdentityReader.ReadCurrent(), out var reason)) throw new IOException("Equipo no autorizado para esta recuperación: " + reason);
            var current = ProductRecoveryInventory.ReadFans();
            if (!ProductRecoveryInventory.AllowsRemaining(selected, current)) throw new IOException("Apareció un registro nuevo o cambió una sesión; no se autorizan llamadas.");
            // Capture every unresolved record before considering any domain-specific hardware operation.
            var n = 0;
            foreach (var record in current) Backup(record.Path, Path.Combine(o.Output, "record-" + n++ + ".before.json"), record.Sha256);
            if (current.Any(x => !x.Recoverable)) throw new IOException("Hay registros dañados, sin identidad o de otro equipo. Se respaldaron; no se escribirá hardware. " + string.Join("; ", current.Where(x => !x.Recoverable).Select(x => x.Path + ": " + x.Problem)));
            var boot = ReadBoot();
            foreach (var record in current) { EnsureFormerOwnersExited(record, o.Owner); PreflightNative(record, boot, archive: null); }
            var entry = selected[o.Index];
            if (!current.Contains(entry)) throw new IOException("La sesión seleccionada ya no coincide con el inventario.");
            RequireIsolation(o.Owner);
            result = entry.Kind == "LegacyFan" ? RecoverLegacy(entry, o, boot) : entry.Kind=="OrphanWmi"?RecoverOrphan(entry,o,boot):RecoverWmi(entry, o, boot);
            succeeded = !ProductRecoveryInventory.Exists(entry.Path);
        }
        catch (Exception ex) { failure = ex.Message; Console.Error.WriteLine(failure); }
        finally { if (owns) mutex.ReleaseMutex(); }
        var report = new { kind = "VictusFanControl.ProductFanRecovery", target = ProductRecoveryInventory.Target, selected, recordIndex = o.Index,
            succeeded, result, failure, automaticStarted = false, normalFanTargets = false, capturedUtc = DateTimeOffset.UtcNow };
        ProductRecoveryInventory.WriteDurable(Path.Combine(o.Output, "fan-recovery-report.json"), JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
        return succeeded && failure is null ? 0 : 4;
    }
    internal static void RequireIsolation(ProductRecoveryOwner owner)
    {
        owner.Validate();
        foreach (var p in Process.GetProcesses()) using (p)
            if (p.Id != Environment.ProcessId && p.Id != owner.Pid &&
                (p.ProcessName.StartsWith("VictusFanControl", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("VictusSetup", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Cierra las otras aplicaciones e instaladores de VictusFanControl normalmente. Proceso activo: " + p.Id);
        using var search = new ManagementObjectSearcher("SELECT Name,State FROM Win32_Service WHERE Name LIKE 'VictusFanControl%'");
        using var rows = search.Get();
        foreach (ManagementObject row in rows) using (row)
            if (!string.Equals(Convert.ToString(row["State"]), "Stopped", StringComparison.OrdinalIgnoreCase)) throw new IOException("El watchdog anterior sigue activo: " + row["Name"] + ". Debe cerrarse normalmente antes de recuperar.");
    }
    private static DateTimeOffset ReadBoot()
    {
        using var search = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"); using var rows = search.Get();
        foreach (ManagementObject row in rows) using (row)
        {
            var boot = new DateTimeOffset(ManagementDateTimeConverter.ToDateTime((string)row["LastBootUpTime"]).ToUniversalTime());
            if (boot >= DateTimeOffset.UtcNow || boot < DateTimeOffset.UtcNow.AddYears(-5)) break;
            QueryInterruptTime(out var uptime);
            if(!KernelBootAgrees(boot,DateTimeOffset.UtcNow,uptime))throw new IOException("Los relojes no acreditan un arranque nuevo del kernel. Usa Reiniciar Windows; Inicio rápido o un cambio de reloj no permiten retirar marcas inciertas.");
            return boot;
        }
        throw new IOException("No se pudo verificar el último arranque de Windows. No se retiran marcas inciertas.");
    }
    internal static bool PreviousBoot(DateTimeOffset marker, DateTimeOffset boot) => marker < boot.AddMinutes(-2);
    internal static bool KernelBootAgrees(DateTimeOffset boot,DateTimeOffset now,ulong uptime100ns) =>
        uptime100ns>0&&uptime100ns<(ulong)TimeSpan.FromDays(3650).Ticks&&Math.Abs((now-TimeSpan.FromTicks((long)uptime100ns)-boot).TotalSeconds)<120;
    [DllImport("kernel32.dll")] private static extern void QueryInterruptTime(out ulong interruptTime100ns);
    private static string SessionDirectory(ProductRecoveryRecord entry)
    {
        if(entry.Kind=="OrphanWmi")return Path.GetDirectoryName(entry.Path)!;
        using var json = JsonDocument.Parse(ProductRecoveryInventory.ReadBytes(entry.Path));
        var path = json.RootElement.GetProperty(entry.Kind == "FanGui" ? "SessionDirectory" : "Directory").GetString()!;
        if (!Path.IsPathFullyQualified(path)) throw new IOException("La carpeta de sesión WMI no es absoluta.");
        var full = Path.GetFullPath(path); ProductRecoveryInventory.EnsurePlainPath(Path.Combine(full, "check")); return full;
    }
    private static void EnsureFormerOwnersExited(ProductRecoveryRecord entry, ProductRecoveryOwner coordinator)
    {
        if(entry.Kind=="OrphanWmi")return;
        using var json = JsonDocument.Parse(ProductRecoveryInventory.ReadBytes(entry.Path)); var r = json.RootElement;
        if (entry.Kind == "LegacyFan")
        {
            var owner=r.GetProperty("Controller"); RejectLive(owner.GetProperty("ProcessId").GetInt32(), owner.GetProperty("ProcessStartUtcTicks").GetInt64()); return;
        }
        var dir=SessionDirectory(entry);
        if(entry.Kind=="FanGui")
        {
            var pid=r.GetProperty("OwnerPid").GetInt32(); var start=r.GetProperty("OwnerStartUtcTicks").GetInt64();
            if(pid!=coordinator.Pid || start!=coordinator.StartUtcTicks)RejectLive(pid,start);
            var ready=Path.Combine(dir,"ready.json");
            if(ProductRecoveryInventory.Exists(ready))
            {
                using var doc=JsonDocument.Parse(ProductRecoveryInventory.ReadBytes(ready));var v=doc.RootElement;
                if(v.GetProperty("OwnerPid").GetInt32()!=pid || v.GetProperty("OwnerStartUtcTicks").GetInt64()!=start ||
                    v.GetProperty("GuardianPid").GetInt32()!=r.GetProperty("GuardianPid").GetInt32() || !v.GetProperty("DirectEcProhibited").GetBoolean())throw new IOException("Identidad READY distinta al registro WMI.");
                RejectLive(v.GetProperty("GuardianPid").GetInt32(),v.GetProperty("GuardianStartUtcTicks").GetInt64());
            }
            else RejectLive(r.GetProperty("GuardianPid").GetInt32(),null);
        }
        else
        {
            var guardian=Path.Combine(dir,"guardian.json");
            if(ProductRecoveryInventory.Exists(guardian))
            {
                using var doc=JsonDocument.Parse(ProductRecoveryInventory.ReadBytes(guardian));var v=doc.RootElement;
                if(v.GetProperty("Pid").GetInt32()!=r.GetProperty("Pid").GetInt32())throw new IOException("Identidad del ensayo WMI distinta al registro.");
                RejectLive(v.GetProperty("Pid").GetInt32(),v.GetProperty("StartTicks").GetInt64());
            }
            else RejectLive(r.GetProperty("Pid").GetInt32(),null);
        }
    }
    private static void RejectLive(int pid,long? ticks)
    {
        using var p=TryProcess(pid);
        if(p is not null && (!ticks.HasValue || p.StartTime.ToUniversalTime().Ticks==ticks))throw new IOException("Una sesión anterior sigue activa. Ciérrala normalmente; no se detienen procesos. PID: "+pid);
    }
    private static void PreflightNative(ProductRecoveryRecord entry, DateTimeOffset boot, string? archive)
    {
        var dir = entry.Kind == "LegacyFan" ? Path.GetDirectoryName(entry.Path)! : SessionDirectory(entry);
        var names = entry.Kind == "LegacyFan" ? new[] { "product-recovery-native-inflight.json" } : new[] { "native-inflight.json", "native-uncertain.signal" };
        foreach (var name in names)
        {
            var path = Path.Combine(dir, name);
            if (!ProductRecoveryInventory.Exists(path)) continue;
            ProductRecoveryInventory.EnsurePlainPath(path);
            var timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            if (!PreviousBoot(timestamp, boot)) throw new IOException("Finalización nativa incierta en " + path + ". Usa Reiniciar Windows y vuelve a recuperar. Suspensión, hibernación e Inicio rápido no acreditan un arranque nuevo.");
            if (archive is not null)
            {
                Backup(path, Path.Combine(archive, name + ".before"));
                // An old native transaction cannot survive the verified kernel restart. Keep its original bytes beside the lease.
                File.Move(path, path + ".previous-boot-" + Guid.NewGuid().ToString("N"));
            }
        }
    }
    private static object RecoverWmi(ProductRecoveryRecord entry, Options o, DateTimeOffset boot) => ExecuteWmi(entry,o.Output,boot,()=>RequireIsolation(o.Owner),
        r=>new HpOmenBiosWmiClient().Send(r),dir=>{WmiFanExperimentBoundary.Enable(dir,true);WmiFanExperimentBoundary.BeginRecovery();});
    internal static object ExecuteWmi(ProductRecoveryRecord entry, string evidence, DateTimeOffset boot, Action ensureIsolation, Func<HpBiosRequest,int> send, Action<string> prepareBoundary)
    {
        VerifyHash(entry); ensureIsolation();
        var dir = SessionDirectory(entry);
        PreflightNative(entry, boot, evidence);
        foreach (var name in new[] { "ready.json", "guardian.json", "guardian-report.json", "summary.json", "write-intent.json", "stop.signal" })
            if (ProductRecoveryInventory.Exists(Path.Combine(dir, name))) Backup(Path.Combine(dir, name), Path.Combine(evidence, name + ".before"));
        VerifyHash(entry);
        using var lease = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stop.signal"), "EXPLICIT_PRODUCT_RECOVERY");
        var hadIntent = ProductRecoveryInventory.Exists(Path.Combine(dir, "write-intent.json"));
        var release = false; var legacy = false;
        if (hadIntent)
        {
            prepareBoundary(dir);
            var session = new WmiFanSession(r =>
            {
                ensureIsolation();
                var rc = send(r);
                if (rc == 0 && r.CommandType == 0x2E) release = true;
                if (rc == 0 && r.CommandType == 0x1A) legacy = true;
                return rc;
            }, _ => throw new InvalidOperationException("Recovery has no normal fan target authority."));
            session.Recover();
        }
        // The whitelist persisted write intent before every target. Absence means no target was admitted.
        PreflightNative(entry, boot, archive: null); VerifyHash(entry); ensureIsolation();
        var result = new { NoWriteIntent = !hadIntent, ReleaseRequestAccepted = release, LegacyDefaultRequestAccepted = legacy,
            IndependentFirmwareOwnershipVerified = false, DirectEcProhibited = true };
        ProductRecoveryInventory.WriteDurable(Path.Combine(evidence, "fan-release-completed.json"), JsonSerializer.SerializeToUtf8Bytes(result));
        lease.Dispose();
        VerifyHash(entry);
        File.Move(entry.Path, entry.Path + ".recovered-" + Guid.NewGuid().ToString("N"));
        return result;
    }
    private static object RecoverOrphan(ProductRecoveryRecord entry,Options o,DateTimeOffset boot) => ExecuteOrphan(entry,o.Output,boot,()=>RequireIsolation(o.Owner));
    internal static object ExecuteOrphan(ProductRecoveryRecord entry,string evidence,DateTimeOffset boot,Action ensureIsolation)
    {
        VerifyHash(entry);ensureIsolation();
        if(ProductRecoveryInventory.Exists(Path.Combine(SessionDirectory(entry),"write-intent.json")))throw new IOException("La sesión sin lease contiene intención de escritura. No se puede reconstruir propiedad.");
        PreflightNative(entry,boot,evidence);
        return new{NoWriteIntent=true,NativeMarkersArchivedAfterKernelRestart=true,FanHardwareWrites=false,IndependentFirmwareOwnershipVerified=false};
    }
    private static object RecoverLegacy(ProductRecoveryRecord entry, Options o, DateTimeOffset boot)
    {
        var module=Path.Combine(o.Modules,"LpcACPIEC.bin");
        if(ProductRecoveryInventory.Hash(File.ReadAllBytes(module))!="c38fd116e7aff4d1fdb0a494e296be0a6708e5a22fc72f14587442fb7f8f7906")throw new IOException("El módulo EC no coincide con el cualificado. No se abrió hardware.");
        var journal = new JsonLeaseJournal(entry.Path, WatchdogTargetPolicies.Hp8C40);
        var record = journal.LoadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult() ?? throw new IOException("El registro anterior desapareció.");
        using var controller = TryProcess(record.Controller.ProcessId);
        if (controller is not null && controller.StartTime.ToUniversalTime().Ticks == record.Controller.ProcessStartUtcTicks)
            throw new IOException("El controlador del watchdog anterior sigue activo. Ciérralo normalmente.");
        // Reuse the qualified old lease manager; external/ambiguous setpoints remain blocked.
        PreflightNative(entry, boot, o.Output); VerifyHash(entry); RequireIsolation(o.Owner);
        var hardware = new TrackedLegacyHardware(o, Path.Combine(Path.GetDirectoryName(entry.Path)!, "product-recovery-native-inflight.json"));
        var manager = new WatchdogLeaseManager(new BoundJournal(journal,entry.Sha256), hardware, new WindowsMonotonicClock());
        var result = manager.RecoverOnStartupAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (result.JournalRetained) throw new IOException("El watchdog anterior no confirmó la liberación: " + result.Detail);
        return result;
    }
    private sealed class BoundJournal(ILeaseJournal inner,string initialHash) : ILeaseJournal
    {
        private string _hash=initialHash;
        public string Path=>inner.Path;
        public WatchdogTargetPolicy TargetPolicy=>inner.TargetPolicy;
        private void Check(){if(ProductRecoveryInventory.Hash(ProductRecoveryInventory.ReadBytes(Path))!=_hash)throw new IOException("El registro del watchdog cambió durante la recuperación.");}
        public async ValueTask<WatchdogLeaseRecord?> LoadAsync(CancellationToken token){Check();var record=await inner.LoadAsync(token);Check();return record;}
        public async ValueTask StoreAsync(WatchdogLeaseRecord record,CancellationToken token){Check();await inner.StoreAsync(record,token);_hash=ProductRecoveryInventory.Hash(ProductRecoveryInventory.ReadBytes(Path));}
        public ValueTask DeleteAsync(CancellationToken token){Check();return inner.DeleteAsync(token);}
    }
    private static Process? TryProcess(int pid) { try { return Process.GetProcessById(pid); } catch (ArgumentException) { return null; } }
    private sealed class TrackedLegacyHardware(Options options, string marker) : ILeaseRecoveryHardware
    {
        private readonly M4Hp8C40LeaseHardware _inner = new(options.Modules);
        private T Track<T>(Func<T> action)
        {
            RequireIsolation(options.Owner);
            ProductRecoveryInventory.WriteDurable(marker, JsonSerializer.SerializeToUtf8Bytes(new { Utc = DateTimeOffset.UtcNow, Pid = Environment.ProcessId }));
            var result = action(); File.Delete(marker); return result; // Failure/death keeps uncertainty across attempts.
        }
        public ValueTask<FanSetpoint> ReadSetpointAsync(CancellationToken token) => ValueTask.FromResult(Track(() => _inner.ReadSetpointAsync(token).GetAwaiter().GetResult()));
        public ValueTask RestoreFirmwareAutoAsync(CancellationToken token) { Track(() => { _inner.RestoreFirmwareAutoAsync(token).GetAwaiter().GetResult(); return true; }); return ValueTask.CompletedTask; }
    }
    private static void VerifyHash(ProductRecoveryRecord entry)
    { if (ProductRecoveryInventory.Hash(ProductRecoveryInventory.ReadBytes(entry.Path)) != entry.Sha256) throw new IOException("El registro cambió antes de recuperar: " + entry.Path); }
    private static void Backup(string source, string destination, string? expected = null)
    {
        var bytes = ProductRecoveryInventory.ReadBytes(source);
        if (expected is not null && ProductRecoveryInventory.Hash(bytes) != expected) throw new IOException("El registro cambió durante el respaldo.");
        ProductRecoveryInventory.WriteDurable(destination, bytes);
        if (!ProductRecoveryInventory.ReadBytes(source).AsSpan().SequenceEqual(bytes)) throw new IOException("La evidencia cambió durante el respaldo.");
    }
}
