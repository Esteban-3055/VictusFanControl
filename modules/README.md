# PawnIO modules

VictusFanControl does not commit PawnIO module binaries to this repository.

Run:

```powershell
.\scripts\setup-pawnio-modules.ps1
```

The script downloads the pinned official **PawnIO.Modules 0.2.11** release, verifies the release ZIP SHA-256, and extracts only:

- `IntelMSR.bin`
- `LpcACPIEC.bin`

The official modules are LGPL-2.1-or-later and are loaded by the already-installed PawnIO driver. VictusFanControl itself communicates with the PawnIO device through `DeviceIoControl`; it does not link to `PawnIOLib.dll`.

The `.bin` files are ignored by Git.
