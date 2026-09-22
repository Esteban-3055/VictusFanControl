# Testing plan

## Baseline objective

Before controlling any fan, characterize the stock HP controller in the machine's **real daily-use configuration**.

For this project, the reference configuration includes the external monitor connected. Because that keeps the RTX 3060 active, this is considered intentional baseline behavior rather than an idle defect.

For every baseline session record:

- room/ambient temperature if known
- power source (AC/battery)
- external monitor connected (expected: yes)
- HP fan/performance mode
- CPU temperature, package power and load
- GPU temperature, power and load
- fan RPM once the read-only RPM provider exists
- scenario and duration

## Recommended baseline sessions

### 1. Idle

- 15 minutes
- external monitor connected
- desktop visible
- no foreground workload
- browser closed if possible

### 2. Light desktop

- 15 minutes
- external monitor connected
- normal browser tabs
- file explorer / messaging / typical low-load use

### 3. Video playback

- 15 minutes
- external monitor connected
- fixed resolution and browser/application noted

### 4. CPU burst workload

Use a repeatable short CPU task to observe how quickly HP increases fans before the heatsink is fully heat-soaked.

### 5. Sustained CPU load

Only after normal temperatures are verified.

### 6. Gaming / combined CPU+GPU

Use one repeatable game scene or benchmark. Record graphics settings and frame-rate limit.

## Fan-control validation

When fan writes are eventually enabled:

- expose one shared RPM target to the control policy
- measure both fan tachometers independently
- verify both fans converge near the common RPM target in low/mid range
- do not assume equal command levels imply equal RPM near saturation
- allow the safety supervisor to break symmetry if required for protection

## Naming convention

```text
logs/2026-09-22_idle_hp-auto.csv
logs/2026-09-22_browser_hp-auto.csv
logs/2026-09-22_video_hp-auto.csv
```

Logs are ignored by Git by default. Upload only deliberately sanitized datasets.
