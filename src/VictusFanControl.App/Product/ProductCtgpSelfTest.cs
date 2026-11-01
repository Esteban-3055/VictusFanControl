using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static class ProductCtgpSelfTest
{
    internal static async Task RunAsync()
    {
        static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
        static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection."); }
        var fake = new Fake(); var control = fake.Create();
        control.Apply(false); Require(fake.Writes.Count == 0 && fake.Qualifications == 0, "Disabled cTGP touched hardware.");
        control.Apply(true); control.Observe(); control.Apply(true);
        Require(control.Status.Active && fake.Writes.Count == 1 && fake.Intents == 1, "Activation was repeated or not confirmed.");
        fake.Ac = false; control.Observe();
        Require(!control.Status.Active && !control.NeedsRestore && fake.Writes.Count == 2 && fake.Writes[^1][0] == 0, "AC loss failed to restore.");
        control.Restore(); Require(fake.Writes.Count == 2, "Completed restoration was repeated.");
        fake.Ac = true; control.Apply(true); control.Apply(false);
        Require(fake.Writes.Count == 4 && !control.Status.Active, "Explicit source re-entry/release failed.");

        fake = new() { Ac = false }; control = fake.Create(); control.Apply(true);
        Require(fake.Writes.Count == 0 && fake.Qualifications == 0 && control.Status.State == "AcOnly", "Battery enabled cTGP.");
        fake = new() { RejectQualification = true }; control = fake.Create(); Reject(() => control.Apply(true));
        Require(fake.Writes.Count == 0, "Qualification rejection still dispatched.");
        fake = new() { Policy = [0, 0, 1, 0] }; control = fake.Create(); Reject(() => control.Apply(true));
        Require(fake.Writes.Count == 0, "Unexpected baseline was overwritten.");
        fake = new() { RejectIntent = true }; control = fake.Create(); Reject(() => control.Apply(true));
        Require(fake.Writes.Count == 0 && !control.NeedsRestore, "Durable-intent failure did not prevent dispatch.");
        fake = new(); control = fake.Create(); control.Apply(true); fake.Policy = [0, 1, 1, 0];
        Reject(control.Observe); control.Restore(); Reject(() => control.Apply(true));
        Require(fake.Writes.Count == 2, "Externally lost policy was re-enabled.");
        fake = new(); control = fake.Create(); control.Apply(true); fake.RejectRestore = true;
        try { control.Restore(); throw new Exception("Expected failed restore."); } catch (HpBiosCallException) { }
        Require(control.NeedsRestore, "Failed restoration discarded its recovery obligation.");
        fake = new(); Hp8C40CtgpControl.RestoreRecorded(r =>
        {
            Require(r.CommandType != 0x22 || r.Payload[0] == 0, "Recorded recovery enabled cTGP.");
            if (r.CommandType == 0x22) fake.Policy = r.Payload.ToArray();
            return new(0, fake.Policy.ToArray());
        });
        var resume = new ProductAutomaticResumption(); resume.Arm(); resume.ConfirmActivation();
        resume.Observe(new ProductRuntimeState { FanMode = "Firmware", LifecycleBlocked = true, CtgpFaulted = true }, 100);
        Require(!resume.Wanted, "cTGP fault retained unattended reactivation intent.");

        Require(Hp8C40CtgpRequestScope.Qualified(new(0x20008, 0x22, [1, 1, 1, 0], 4)), "Qualified request rejected.");
        Require(!Hp8C40CtgpRequestScope.Qualified(new(0x20008, 0x22, [80, 1, 1, 0], 4)), "Numeric watts escaped whitelist.");
        Require(!Hp8C40CtgpRequestScope.Qualified(new(0x20008, 0x22, [1, 1, 1, 87], 4)), "Thermal byte modification admitted.");
        Require(!Hp8C40CtgpRequestScope.Qualified(new(0x20008, 0x2E, [50, 50, 0, 0], 0)), "cTGP scope authorized fans.");
        var defaults = new ProductProfiles(); Require(!defaults.CtgpEnabled, "cTGP default is not opt-in.");
        Require(ProductProfilesStore.Copy(defaults with { CtgpEnabled = true }).CtgpEnabled, "cTGP preference did not roundtrip.");
        var legacy = JsonNode.Parse(ProductProfilesStore.Serialize(defaults))!.AsObject(); legacy.Remove("ctgpEnabled");
        Require(!ProductProfilesStore.Parse(legacy.ToJsonString()).CtgpEnabled, "Legacy profile enabled cTGP.");

        using (var form = new ProductForm("fixture://modules", fixture: new ProductGuiSelfTest.RecordingRuntime(), fixtureProfiles: defaults))
        {
            form.Canvas.Page = ProductPage.Performance; form.Canvas.PerformanceTab = 2;
            form.ClientSize = new(1672, 941); form.PerformLayout();
            form.HandleCommand("ctgp-toggle"); Require(form.Draft.CtgpEnabled, "cTGP toggle did not edit preference.");
            Require(form.Canvas.State.CtgpState == "Disabled", "Preference editing pretended to activate hardware.");
            foreach (var size in new[] { new Size(1672, 941), new Size(1040, 660) })
            {
                form.ClientSize = size; form.PerformLayout();
                using var bitmap = new Bitmap(form.Canvas.Width, form.Canvas.Height);
                form.Canvas.DrawToBitmap(bitmap, new(0, 0, bitmap.Width, bitmap.Height));
                Require(form.Canvas.Hits.Count(h => h.Id.StartsWith("perf-tab-")) == 5, "Five performance tabs missing.");
                Require(form.Canvas.Hits.Any(h => h.Id == "ctgp-toggle" && h.Enabled), "cTGP option inaccessible.");
                Directory.CreateDirectory("logs/ctgp-self-test"); bitmap.Save($"logs/ctgp-self-test/ctgp-{size.Width}.png", ImageFormat.Png);
            }
            form.Canvas.State = form.Canvas.State with { FanMode = "Automatic" };
            form.HandleCommand("ctgp-toggle"); Require(form.Draft.CtgpEnabled, "Toggle changed an active session without Firmware.");
        }

        var folder = Path.GetFullPath(Path.Combine("logs/ctgp-self-test", "guardian-" + Guid.NewGuid().ToString("N")));
        await using var guardian = new WmiFanGuiGuardianClient(fixture: true, fixtureDirectory: folder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await guardian.StartAsync(timeout.Token).ConfigureAwait(false); await guardian.SetCtgpAsync(true, timeout.Token).ConfigureAwait(false);
        Require(guardian.Ctgp is { Active: true }, "Fixture IPC activation not confirmed.");
        await guardian.SetCtgpAsync(false, timeout.Token).ConfigureAwait(false);
        Require(guardian.Ctgp is { Active: false }, "Fixture IPC restoration not confirmed.");
        var released = await guardian.ReleaseAsync(timeout.Token).ConfigureAwait(false);
        Require(released.GuardianLeaseRetired, "cTGP fixture lease not retired.");
        Console.WriteLine("PASS: cTGP opt-in, fixed whitelist, AC-only lifecycle, external-policy rejection, failed restore retention, profile roundtrip, background UI renders and detached guardian fixture IPC. No hardware writes.");
    }

    private sealed class Fake
    {
        internal bool Ac = true, RejectQualification, RejectIntent, RejectRestore;
        internal byte[] Policy = [0, 1, 1, 0];
        internal int Qualifications, Intents;
        internal List<byte[]> Writes = [];
        internal Hp8C40CtgpControl Create() => new(r =>
        {
            if (r.CommandType == 0x28) { var data = new byte[128]; data[0] = 200; return new(0, data); }
            if (r.CommandType == 0x22)
            {
                Writes.Add(r.Payload.ToArray());
                if (RejectRestore && r.Payload[0] == 0) return new(5, new byte[4]);
                Policy = r.Payload.ToArray();
            }
            return new(0, Policy.ToArray());
        }, () => Ac, () => { Qualifications++; if (RejectQualification) throw new InvalidOperationException("fixture qualification"); },
            () => { if (RejectIntent) throw new InvalidOperationException("fixture intent"); Intents++; });
    }
}
