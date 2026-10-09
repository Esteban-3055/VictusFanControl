VictusFanControl 0.4.0-rc.1
Software Release Candidate - HP 8C40 / 9D0R1LA / BIOS F.18

THIS IS A PRE-P15 SOFTWARE RC.

Safety state:
- Fan control is OFF by default.
- Manual execution authorization is CLOSED.
- Automatic execution authorization is CLOSED.
- Candidate V1 is not physically validated or production-authorized.
- The package does not change those gates.

Runtime:
- Windows 11 x64.
- .NET 8 Desktop Runtime x64 is required (framework-dependent publish).
- PawnIO driver must already be installed.
- The package contains only the two pinned PawnIO module files required by VictusFanControl; it does not install the PawnIO driver.

Layout:
- app\      GUI and dependencies; app\modules contains IntelMSR.bin and LpcACPIEC.bin.
- watchdog\ production watchdog and dependencies; watchdog\modules contains LpcACPIEC.bin.
- release\  release contracts and package metadata.
- PACKAGE-MANIFEST.json contains SHA-256 for every payload file except itself.

P15 remains the first target-side post-P13 validation checkpoint.
