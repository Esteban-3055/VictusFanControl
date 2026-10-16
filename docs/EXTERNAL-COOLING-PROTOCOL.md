# Victus external cooling protocol v1

Development target: ESP32-C3 TCP server; Windows TCP client. Firmware is **not** supplied.
TCP port 8765 by default; hostname `victus-base.local`, manual IPv4/IPv6 or DNS hostname.
Local LAN only; no discovery broadcast, cloud, HTTP API, port forwarding or server dependency.

## Framing and limits

UTF-8 JSON object followed by **LF**, at most 4096 bytes excluding LF; JSON depth <=8.
One frame per line, no pretty-printing. TCP fragments and coalesced frames are supported.
Whole-frame read deadline 5 seconds, connect deadline 5 seconds, write deadline 3 seconds.
A partial frame does not renew its deadline. EOF discards any unfinished frame.
Duplicate property names, invalid UTF-8/JSON, unsupported version/type and invalid values
close the session. Unknown properties are ignored for additive evolution; unknown message
types require a protocol revision and are rejected. Do not reuse a failed session.

Envelope (all fields required):

```json
{"protocolVersion":1,"type":"telemetry","sessionId":"32-lowercase-hex-GUID","sequence":1,"payload":{},"mac":"base64-HMAC"}
```

`sessionId` is a new random GUID in N format chosen by Windows on every TCP connection.
Handshake sequence=0; operational sequences start at 1, independently in each direction,
and must strictly increase (int64 positive; gaps allowed). No sequence rollover in a session.
Only authenticated deviceStatus can acknowledge a telemetry frame; TCP connection alone
does not mean Connected. No telemetry or remote request before mutual authentication.

## Pairing and mutual authentication

Provision the same **random 32-byte key**, encoded Base64 for human transfer, on both peers
out of band. Windows generates a cryptographically random key and saves it encrypted with
DPAPI CurrentUser. Never send the key, log it, pass it on a command line or embed it in firmware
published to a repository. The simulator accepts it at a hidden console prompt.

Each nonce is 32 cryptographically random bytes encoded standard Base64 (including padding).
Handshake frames have empty `mac`. Exchange:

1. Windows `hello`: payload `{ "clientNonce": "..." }`.
2. Device `challenge`: `{ "serverNonce": "...", "proof": "..." }`, role `server`.
3. Windows `authenticate`: `{ "proof": "..." }`, role `client`.
4. Device `authenticated`: `{ "proof": "..." }`, role `ready`.

Every proof is standard Base64 of HMAC-SHA256(key, UTF8(transcript)), with this exact transcript
(LF separators, **no trailing LF**):

```text
VictusExternalCooling/v1/auth
ROLE
SESSION_ID
CLIENT_NONCE_BASE64
SERVER_NONCE_BASE64
```

Compare all proofs in constant time. Roles prevent reflection. Each peer verifies the exact
session, type, sequence 0, empty envelope mac and nonce length before accepting the next step.
Derive directional 32-byte MAC keys using the same transcript, with role `client-to-device`
or `device-to-client`; use the **raw HMAC bytes**, not the UTF-8 Base64 string.

Operational mac is Base64(HMAC-SHA256(directional key, UTF8(transcript))):

```text
VictusExternalCooling/v1/frame
TYPE
SESSION_ID
SEQUENCE_DECIMAL
PAYLOAD_RAW_JSON
```

No trailing LF; sequence uses invariant decimal without leading zeroes. `PAYLOAD_RAW_JSON`
means the exact UTF-8 substring for the payload object, including any escaping/whitespace.
Preserve these bytes when sending and extract their original span when verifying; do not
parse and reserialize before checking the MAC. Envelope property ordering is immaterial.
Windows' encoder preserves the payload bytes used to compute the signature. HMAC authenticates
integrity and origin; telemetry is not encrypted. Use on a trusted LAN. Key strength is mandatory:
this is not a password protocol and a human password would permit offline guessing.

Independent deterministic [HMAC test vectors](EXTERNAL-COOLING-V1-VECTORS.json) include all
roles, directional key derivations and an operational deviceStatus MAC. They were calculated
independently with Python's standard hmac/hashlib and are checked by the C# suite. The fixture
key is public and **must never be provisioned** on a device or used as a pairing default.

## Telemetry payload

Every field is present. Null means unavailable/invalid/expired; never substitute zero.

| Field | Type / units | Nullability / meaning |
|---|---|---|
| validForMs | integer | 2000; maximum lifetime from receipt, never renewed by duplicate frames |
| replyToDeviceSequence | integer or null | Latest validated device reply; device checks its own send timestamp |
| capturedAtUtc | ISO 8601 timestamp | Required; Windows export time |
| sampledAtUtc | timestamp or null | Original current CPU/GPU snapshot time |
| decisionAtUtc | timestamp or null | Original accepted automatic decision |
| cpuTempSampledAtUtc, gpuTempSampledAtUtc | timestamp or null | Original thermal acquisition times |
| ramSampledAtUtc | timestamp or null | GlobalMemoryStatusEx acquisition time |
| cpuUsagePct, gpuUsagePct, ramUsagePct | number or null, % | Finite 0..100 |
| demandaPct | number or null, % | Accepted current automatic UnifiedDemand.Percent, 0..100 |
| solicitud | integer or null, logical level | Accepted supervised controller request, **not PWM** |
| solicitudValida | boolean | True iff solicitud is current and non-null |
| cpuTargetLevel, gpuTargetLevel | integer or null, level | Observed backend mapping, may differ |
| modoSolicitado | string | Firmware, Manual, Automatic; Automatic while preparing |
| modoEfectivo | string | Firmware, Manual, Automatic, Transition, Interrupted, Unavailable |
| cpuTempC, gpuTempC | number or null, °C | Finite -20..130, acquisition independently expires |
| powerSource | string | AC, Battery, Unknown |
| requestEvidence | string | WmiAcceptedSupervised, Unavailable, MonitorOnly, AwaitingDevice |

Integer wire bounds are 0..65535, **not** the controller's operating limits. Current controller
is a single logical request with separate physical destinations (up to 54/52 in v1.2.9).
Never infer a permanent level range or map levels to PWM in Windows. Device firmware must
define its own bounded policy later. WmiAcceptedSupervised means an accepted native WMI request
in a live supervised session; it is not independent physical setpoint/ownership verification.

Windows invalidates requests in Firmware, unhealthy/expired telemetry, retained critical samples,
preparation, source/performance transition, recovery or loss of authority. Automatic additionally
requires the accepted decision to match the accepted level, current profile and active session.
Manual requires an explicit accepted Manual Apply in the current mode and current healthy
supervised state, not the UI slider or an Automatic target retained across a mode change.
MonitorOnly always clears solicitud, solicitudValida and physical targets; demand remains data.

For a valid request, sampledAtUtc and both thermal timestamps must be non-null, non-default,
not after capturedAtUtc, and less than 5000 ms before it. Both temperatures must be present.
Automatic also requires a non-null demand and a decisionAtUtc meeting that same age bound.
This v1 defensive upper bound does not replace the Windows adapter's shorter sensor/controller
age limits. Compare timestamps within the message; do not compare them with the device's UTC
clock. A fresh MAC, sequence or reply reference never makes old source evidence current.

Device must stop applying a remote request on EOF, failed authentication, invalid message,
session change, explicit null/false request, or **2000 ms without a new valid telemetry frame**.
Additionally, **before applying** any valid remote request, look up `replyToDeviceSequence` in
the last 16 device reply send timestamps. That reply must have been sent less than 2000 ms ago
on the device's monotonic clock. Unknown/null/expired reply references force Local fallback.
This bounds the age of commands delayed inside TCP buffers without clock synchronization.
Windows sends no request in its first telemetry, and clears requests as AwaitingDevice if no
valid device reply arrived in the preceding 2000 ms. MonitorOnly uses its own suppression reason.
Use a monotonic local timer, not Windows wall time, for the remote lease. A duplicate or invalid
frame must not renew it. The simulator implements this reply-reference admission and an
independent 100 ms timer that expires its simulated Remote state after 2000 ms without a
new admitted request. EOF, cancellation and rejected sessions reset that state to Local.
It does not emit unsolicited status frames. Future firmware must implement and physically
validate its own timer-driven PWM fallback on silence/EOF; simulation is not physical proof.

## Device response payload

Respond to each telemetry with an authenticated `deviceStatus`:

```json
{"ackSequence":1,"ambientTempC":23.4,"appliedPwmPct":25,"mode":"Local","communication":"Ready","extensions":{"slot1":null,"slot2":null}}
```

All fields required: ambientTempC nullable finite -40..100 °C; appliedPwmPct nullable finite
0..100%; mode Local/Remote; communication Ready/Timeout/Fault. `ackSequence` must reference a
sent telemetry frame, strictly increase, and arrive within 5 seconds of that send. This detects
delayed traffic without requiring an ESP32 real-time clock/NTP. Windows retains at most 16 send
timestamps. Replies from previous sessions or duplicate device sequences are rejected.

`extensions.slot1` and `slot2` are reserved opaque JSON/null, with **no interpretation or action**.
Future values obey the same frame/depth bounds. Diagnostics/UI strip their contents; the parser
accepts them for compatibility. Device reports are informational: they never change notebook
fan authority, power limits, profiles, protections, recovery or settings.

## Scheduling and lifecycle

Windows pulls current telemetry once per second, with one write in flight and zero queued
snapshots. No old backlog is replayed after a slow write/reconnect. Exponential reconnect
500 ms..30 s plus 0..249 ms jitter, resetting after a valid session. User disconnect suppresses
reconnect until explicit Connect or new configuration. Disabled never opens a socket.
Suspend cancels immediately; resume drains the old worker and authenticates a new session.
Minimize/close-to-tray does not suspend this service. Application exit disposes it separately.

Limits and deadlines keep ESP32-C3 memory bounded. Minimum device resources: <=4096 byte input
frame, one output frame, SHA256/HMAC context, 2 nonces and 2 derived keys, sequence counters,
and 16 device reply sequence/send-time pairs for the monotonic request lease.
