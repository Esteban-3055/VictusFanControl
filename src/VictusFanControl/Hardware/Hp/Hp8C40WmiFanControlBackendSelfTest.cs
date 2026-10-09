using VictusFanControl.Control;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

internal static class Hp8C40WmiFanControlBackendSelfTest
{
    internal static async Task<int> RunAsync(TextWriter output)
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var guardian = new FakeGuardian();
        var requests = new List<HpBiosRequest>();
        long sequence = 0;
        ValueTask<HpWmiFanProofSample> Read(CancellationToken t) => ValueTask.FromResult(new HpWmiFanProofSample(
            new HpWmiFanTelemetrySample(30, 29, DateTimeOffset.UtcNow, Environment.TickCount64), ++sequence));
        var backend = new Hp8C40WmiFanControlBackend(guardian, r => { requests.Add(r); return 0; }, Read);
        await backend.GetStatusAsync(default);
        Require(!guardian.Started && requests.Count == 0, "Firmware status dispatched a setter/guardian.");
        await backend.EnterCustomModeAsync(default);
        Require(guardian.Started && requests.Count == 0, "Admission must arm guardian without fan write.");
        await backend.ApplyAsync(new(30, 30, "test"), default);
        await backend.ApplyAsync(new(30, 30, "hold"), default);
        Require(requests.Count == 1 && guardian.Intents == 1, "Equal target was retransmitted.");
        await backend.ApplyAsync(new(31, 31, "increase"), default);
        Require(requests.Count == 2 && guardian.Intents == 2, "Changed target was lost.");
        var status = await backend.GetStatusAsync(default);
        Require(status.CustomModeActive && status.Detail.Contains("ownership unverified"), "RPM misrepresented hardware ownership.");
        var hb = guardian.Heartbeats;
        await backend.ProbeControlDependencyAsync(default);
        Require(guardian.Heartbeats == hb, "Liveness-only probe renewed heartbeat.");
        guardian.Alive = false;
        var rejected = false;
        try { await backend.ApplyAsync(new(10, 10, "lost"), default); } catch (IOException) { rejected = true; }
        Require(rejected && requests.Count == 2, "Guardian loss admitted hardware write.");
        guardian.Alive = true;
        await backend.RestoreFirmwareAutoAsync(default);
        Require(backend.ReleaseEvidence is { GuardianLeaseRetired: true, IndependentFirmwareOwnershipVerified: false } &&
            !backend.LastRestoreEvidence.LocalFirmwareAckVerified, "WMI acceptance promoted to EC ownership proof.");
        await backend.EnterCustomModeAsync(default);
        await backend.ApplyAsync(new(32, 32, "rearmed"), default);
        await backend.RestoreFirmwareAutoAsync(default);
        Require(guardian.StartCalls == 2 && guardian.Releases == 2 && requests.Count == 3 &&
            requests[^1].Payload[0] == 32,
            "Verified release did not rearm a fresh supervised WMI session.");
        await output.WriteLineAsync("PASS: WMI admission, changed-target writes only, fresh RPM, no blind heartbeat, guardian-loss fence, truthful release and verified rearm.");

        var rangeGuardian = new FakeGuardian();var rangeCalls = new List<HpBiosRequest>();
        var manualRange = new Hp8C40WmiFanControlBackend(rangeGuardian, r => { rangeCalls.Add(r); return 0; }, Read);
        Require(manualRange.Capabilities.MinimumLevel==10&&manualRange.Capabilities.MaximumLevel==50&&!manualRange.Capabilities.SupportsIndependentLevels,"Manual capabilities lost equal-only 10..50.");
        await manualRange.EnterCustomModeAsync(default);
        foreach(var invalid in new[]{new FanCommand(9,9,"low"),new FanCommand(51,51,"high"),new FanCommand(10,11,"asymmetric")})
        {
            rejected=false;try{await manualRange.ApplyAsync(invalid,default);}catch(ArgumentException){rejected=true;}
            Require(rejected&&rangeCalls.Count==0&&rangeGuardian.Intents==0,"Invalid Manual command persisted or dispatched.");
        }
        foreach(var target in new[]{10,29,50})await manualRange.ApplyAsync(new(target,target,"qualified Manual range"),default);
        await manualRange.ApplyAsync(new(50,50,"hold"),default);
        Require(rangeCalls.Select(r=>(int)r.Payload[0]).SequenceEqual(new[]{10,29,50})&&rangeCalls.All(r=>r.Payload[0]==r.Payload[1])&&rangeGuardian.Intents==3,"Manual low range payload/deduplication mismatch.");
        await manualRange.RestoreFirmwareAutoAsync(default);await manualRange.EnterCustomModeAsync(default);
        await manualRange.ApplyAsync(new(10,10,"rearmed low endpoint"),default);
        Require(rangeCalls.Count==4&&rangeCalls[^1].Payload[0]==10,"Rearmed session lost Manual minimum.");
        await manualRange.RestoreFirmwareAutoAsync(default);
        await output.WriteLineAsync("PASS: GUI WMI equal Manual 10/29/50, invalid/asymmetric refusal before intent, duplicate suppression and low-end rearm; no hardware IO.");

        var failing = new FakeGuardian { RejectIntent = true };
        var dispatches = 0;
        var b = new Hp8C40WmiFanControlBackend(failing, _ => { dispatches++; return 0; }, Read);
        await b.EnterCustomModeAsync(default);
        rejected = false;
        try { await b.ApplyAsync(new(10, 10, "intent failure"), default); } catch (IOException) { rejected = true; }
        Require(rejected && dispatches == 0, "Failed durable intent permitted dispatch.");
        failing.RejectIntent = false;
        var nativeFailure = new Hp8C40WmiFanControlBackend(failing, _ => { dispatches++; return 7; }, Read);
        await nativeFailure.EnterCustomModeAsync(default);
        rejected = false;
        try { await nativeFailure.ApplyAsync(new(30, 30, "rejected"), default); } catch (HpBiosCallException) { rejected = true; }
        Require(rejected && dispatches == 1, "Native rejection retried setter.");
        rejected = false;
        try { await nativeFailure.ApplyAsync(new(30, 30, "retry"), default); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected && dispatches == 1, "Native failure allowed normal phase to reopen.");
        await output.WriteLineAsync("PASS: failed intent prevents dispatch; rejected native setter is never retried.");
        var lost = new FakeGuardian { RejectRelease = true };
        var recoveryCalls = new List<HpBiosRequest>();
        var orphan = new Hp8C40WmiFanControlBackend(lost, r => { recoveryCalls.Add(r); return 0; }, Read);
        await orphan.EnterCustomModeAsync(default);
        await orphan.ApplyAsync(new(30, 30, "orphan"), default);
        rejected = false;
        try { await orphan.RestoreFirmwareAutoAsync(default); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected && recoveryCalls.Count == 3 && recoveryCalls[1].Payload[0] == 255 &&
            recoveryCalls[2].CommandType == 0x1A && orphan.ReleaseEvidence is null,
            "Guardian failure did not attempt local WMI recovery / falsely retired lease.");
        await output.WriteLineAsync("PASS: supervisor failure permits only drained local release/default; lease remains unqualified.");
        return 0;
    }
    private sealed class FakeGuardian : IWmiFanGuiGuardian
    {
        public bool Started, Alive = true, RejectIntent, RejectRelease;
        public int Intents, Heartbeats, StartCalls, Releases;
        public string SessionDirectory => "fixture";
        public string ReportPath => "fixture/report.json";
        public Task StartAsync(CancellationToken t) { Started = true; StartCalls++; return Task.CompletedTask; }
        public void EnsureAlive() { if (!Started || !Alive) throw new IOException("Guardian lost"); }
        public void PersistIntent(int level) { if (RejectIntent) throw new IOException("Intent failed"); Intents++; }
        public void Heartbeat() { Heartbeats++; }
        public Task<FanWmiReleaseEvidence> ReleaseAsync(CancellationToken t)
        {
            Releases++;
            if (RejectRelease)
                return Task.FromException<FanWmiReleaseEvidence>(new IOException("Guardian lost"));
            Started = false;
            return Task.FromResult(new FanWmiReleaseEvidence(true, true, true, false, ReportPath, "CLIENT_RELEASE"));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
