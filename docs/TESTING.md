# Testing plan

## Baseline objective

Before controlling any fan, characterize the stock HP controller.

For every baseline session record:

- room/ambient temperature if known
- power source (AC/battery)
- HP fan/performance mode
- CPU temperature, package power and load
- GPU temperature, power and load
- fan RPM once the read-only RPM provider exists
- scenario and duration

## Recommended baseline sessions

### 1. Idle

- 15 minutes
- desktop visible
- no foreground workload
- browser closed if possible

### 2. Light desktop

- 15 minutes
- normal browser tabs
- file explorer / messaging / typical low-load use

### 3. Video playback

- 15 minutes
- fixed resolution and browser/application noted

### 4. CPU burst workload

Use a repeatable short CPU task to observe how quickly HP increases fans before the heatsink is fully heat-soaked.

### 5. Sustained CPU load

Only after normal temperatures are verified.

### 6. Gaming / combined CPU+GPU

Use one repeatable game scene or benchmark. Record graphics settings and frame-rate limit.

## Naming convention

```text
logs/2026-09-22_idle_hp-auto.csv
logs/2026-09-22_browser_hp-auto.csv
logs/2026-09-22_video_hp-auto.csv
```

Logs are ignored by Git by default. Upload only deliberately sanitized datasets.
