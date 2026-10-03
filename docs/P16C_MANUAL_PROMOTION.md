# P16C — ordinary Manual

Part 1 of the Manual/editor work promotes only Manual for exact profile
HP-8C40-9D0R1LA-F18. Basis: physical attempt 7 on 70bffae6, audited ZIP SHA-256
f9ddcf052474d37270726ab429c6a9a0ac49438d45aad7573ea38924350416be,
closure 335c9dec2f30bf392e84e7a8d0ab487bf8e91fb8 and CI #1218 /
37100418375 SUCCESS. The promotion's exact HEAD must pass CI before use.

Every startup stays in Firmware. Select Manual and explicitly Apply an equal
CPU/GPU level in the existing production range 10..50. The saved level is only
a UI preference; no mode, ownership or automatic enablement is restored.
Selecting Manual alone sends no command. Firmware and tray Exit retain the
qualified release path. WMI proofs, EC ownership/guards, SafetyGate thermal and
freshness limits, native read admission and installed M4 watchdog are unchanged.
The accepted command proof is now logged for ordinary Manual as well.

P16 qualification gates stay closed, all consumed fences/history stay intact,
and isolated old qualification modes cannot inherit ordinary Manual authority.
Legacy 88F8 and unknown profiles cannot receive this promotion. Automatic,
Candidate V1 and control by default remain disabled. Historical P13/P14/P15
software checkpoints are preserved; their source-gate assertions now validate
the current evidence-backed P16C boundary instead of requiring historical closure
forever. The common validation requires the exact P16 PASS and closure CI basis.

The new deterministic cases cover exact-target isolation, startup Firmware,
no command before mode selection, no write on selecting Manual and refusal of
Automatic. Existing tests cover level range, duplicate suppression, stale-safety
refresh and release. CI also validates the promotion in Windows PowerShell 5.1.

Physical attempt 7 proves 30/40/30 and release on this machine, not endurance or
physical calibration of every level in 10..50. Firmware can regain authority
when safety, ownership or dependencies fail; the UI/log report the outcome.

After this exact HEAD passes CI, update the branch and build the app:

```powershell
git pull --ff-only
dotnet build .\VictusFanControl.sln -c Release -warnaserror
if ($LASTEXITCODE -eq 0) {
    .\src\VictusFanControl.App\bin\Release\net8.0-windows\VictusFanControl.App.exe --modules-dir .\modules
}
```

Use the ordinary app, not the consumed P16 harness.
