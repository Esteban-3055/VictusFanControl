# Contributing

This project interacts with laptop thermal-control hardware. Contributions that add write access must be reviewed more strictly than ordinary telemetry changes.

## Ground rules

1. Read-only telemetry changes may be developed normally.
2. Fan-control changes must preserve the invariants in `docs/SAFETY.md`.
3. Never add a board ID to the control allowlist based only on similarity to another model.
4. Never commit serial numbers, machine GUIDs, user names, local paths, or raw logs containing private identifiers.
5. Hardware behavior must be documented with the exact board Product ID and test conditions.
6. Prefer BIOS/WMI interfaces over undocumented direct EC writes when both are known to work.
7. Any copied or adapted third-party code must preserve its license and attribution.

## Pull requests

Include:

- What changed.
- Why it is safe.
- Which board IDs were tested.
- Before/after telemetry if control behavior changed.
- Recovery behavior if the new code fails.
