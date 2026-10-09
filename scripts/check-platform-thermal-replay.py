#!/usr/bin/env python3
"""Independent source admission, MAX-demand, baseline and trace reconciliation."""
import argparse, gzip, hashlib, importlib.util, json, math, pathlib

spec = importlib.util.spec_from_file_location('source_epochs', pathlib.Path(__file__).with_name('prepare-oem-shadow-replay.py'))
epochs = importlib.util.module_from_spec(spec); spec.loader.exec_module(epochs)
stamp = epochs.timestamp

def read(path):
    data = gzip.decompress(path.read_bytes()) if path.suffix == '.gz' else path.read_bytes()
    return [json.loads(line) for line in data.splitlines()]

def signature(rows):
    data = [[r['timestamp'], None if r['baseline'] is None else
             [r['baseline'][k] for k in ('accepted','equalFanLevel','thermalOverride','sustainedLoadCooling')]] for r in rows]
    return hashlib.sha256(json.dumps(data, separators=(',', ':')).encode()).hexdigest()

def interpolate(points, value, key='temperatureC'):
    if value <= points[0][key]: return points[0]['level']
    for left, right in zip(points, points[1:]):
        if value <= right[key]:
            return left['level']+(right['level']-left['level'])*(value-left[key])/(right[key]-left[key])
    return points[-1]['level']

def fresh(source, now, max_age):
    if not source or source.get('Value') is None or not source.get('SampledAtUtc'): return False
    value = source['Value']; age = now-stamp(source['SampledAtUtc'])
    return math.isfinite(value) and 0 <= value <= 120 and 0 <= age < max_age*10_000_000

class Admission:
    def __init__(self, tz, dtt, settings):
        self.enabled = [tz, dtt]; self.settings = settings
        self.last = None; self.channels = [None, None]
    def evaluate(self, frame, history=None):
        if not any(self.enabled): return True
        now = stamp(frame['TimestampUtc']); s = self.settings
        if self.last is not None and (now <= self.last or now-self.last > s['maximumFrameGapSeconds']*10_000_000):
            self.last = now; self.channels = [None, None]; return False
        self.last = now; qualified = []
        for i, key in enumerate(('Tz01', 'Dtt3')):
            if not self.enabled[i]: qualified.append(True); continue
            src = frame[key]
            if not fresh(src, now, s['maximumSourceAgeSeconds']):
                self.channels[i] = None; qualified.append(False); continue
            state = self.channels[i]
            samples = [src] if history is None else history[key]
            if history is not None:
                valid = 1 <= len(samples) <= 8 and samples[-1] == src
                previous = None
                for sample in samples:
                    at = stamp(sample['SampledAtUtc']) if sample and sample.get('SampledAtUtc') else None
                    valid = valid and at is not None and at <= now and fresh(sample, at, s['maximumSourceAgeSeconds'])
                    valid = valid and (previous is None or at > previous)
                    previous = at
                if state is not None and stamp(src['SampledAtUtc']) < state['last']: valid = False
                if not valid:
                    self.channels[i] = None; qualified.append(False); continue
            observed = None if state is None else state['last']
            ok = False
            for sample in samples:
                at = stamp(sample['SampledAtUtc']); value = sample['Value']
                if history is not None:
                    if observed is not None and at < observed: continue
                    if observed is None and not fresh(sample, now, s['maximumSourceAgeSeconds']): continue
                if state is not None and (at < state['last'] or (at == state['last'] and value != state['value']) or
                                          at-state['last'] > s['maximumSourceAgeSeconds']*10_000_000):
                    state = None; ok = False; break
                if state is None: state = dict(first=at, last=at, value=value, count=1)
                elif at != state['last']: state.update(last=at, value=value, count=state['count']+1)
                ok = state['count'] >= s['qualificationAcquisitions'] and at-state['first'] >= s['qualificationSeconds']*10_000_000
            self.channels[i] = state
            qualified.append(ok and state['last'] == stamp(src['SampledAtUtc']))
        return all(qualified)

def main():
    p = argparse.ArgumentParser()
    p.add_argument('fixtures', type=pathlib.Path); p.add_argument('platform_fixtures', type=pathlib.Path)
    p.add_argument('output', type=pathlib.Path); p.add_argument('--compress', action='store_true')
    a = p.parse_args(); summary = json.loads((a.output/'summary.json').read_text(encoding='utf-8-sig'))
    assert summary['productionEnabled'] is False and summary['hardwareWrites'] is False
    golden = json.loads((a.platform_fixtures/'baseline-signatures.json').read_text(encoding='utf-8-sig'))
    profiles = {p['name']: p['fan'] for p in summary['profiles']}; rows_checked = 0
    recorded_path = a.platform_fixtures/'recorded-fan-settings.json'
    assert hashlib.sha256(recorded_path.read_bytes()).hexdigest() == summary['recordedSettingsHash']
    recorded = json.loads(recorded_path.read_text(encoding='utf-8-sig'))
    assert profiles['recorded-ac'] == recorded['ac'] and profiles['recorded-battery'] == recorded['battery']
    for session in ('v10', 'v11'):
        path = a.platform_fixtures/(session+'.cores.jsonl.gz')
        assert hashlib.sha256(path.read_bytes()).hexdigest() == summary['archivedCoreHashes'][session]
        manifest = json.loads((a.platform_fixtures/(session+'.cores.manifest.json')).read_text(encoding='utf-8-sig'))
        assert hashlib.sha256(gzip.decompress(path.read_bytes())).hexdigest() == manifest['uncompressedSha256']
    for run in summary['runs']:
        session, profile, variant = (run[k] for k in ('session','profile','variant'))
        fixture = a.fixtures/(session+'.jsonl.gz')
        assert hashlib.sha256(fixture.read_bytes()).hexdigest() == summary['sourceHashes'][session]
        frames = read(fixture); fan = profiles[profile]; tuning = fan['tuning']; demand = fan['unifiedDemand']
        core_path = a.platform_fixtures/(session+'.cores.jsonl.gz')
        cores = {stamp(r['timestampUtc']): r['cores'] for r in read(core_path)} if core_path.exists() else {}
        path = a.output/f'{session}-{profile}-{variant}.jsonl'
        if not path.exists(): path = pathlib.Path(str(path)+'.gz')
        rows = read(path); assert len(rows) == len(frames) == run['frames']
        assert signature(rows) == golden['signatures'][f'{session}-{profile}'], 'Original baseline changed'
        use_tz = variant not in ('baseline','dtt3'); use_dtt = variant not in ('baseline','tz01')
        admission = Admission(use_tz, use_dtt, summary['parameters'])
        tz_shift = 5 if variant == 'both-warmer-thresholds' else -5 if variant == 'both-colder-thresholds' else 0
        dtt_shift = 3 if tz_shift > 0 else -3 if tz_shift < 0 else 0
        tz_curve = [dict(p, temperatureC=p['temperatureC']+tz_shift) for p in summary['parameters']['tz01Curve']]
        dtt_curve = [dict(p, temperatureC=p['temperatureC']+dtt_shift) for p in summary['parameters']['dtt3Curve']]
        counts = dict(baseAccepted=0, accepted=0, unavailable=0, raisedTargets=0, rawDemandBelowBaseline=0,
                      targetsBelowBaseline=0, overrideMismatch=0, maximumExtraLevels=0, candidateChanges=0, baselineChanges=0)
        totals = dict(pairedSeconds=0., extraSeconds=0., extraLevelSeconds=0., lowPowerExtraSeconds=0., highPowerExtraSeconds=0.)
        previous = None; remembered = None; last_shadow = None; histogram = {}
        for frame, row in zip(frames, rows):
            now = stamp(frame['TimestampUtc']); assert stamp(row['timestamp']) == now
            available = admission.evaluate(frame); assert available == row['platform']['available']
            if available:
                tz = interpolate(tz_curve, frame['Tz01']['Value']) if use_tz else None
                dtt = interpolate(dtt_curve, frame['Dtt3']['Value']) if use_dtt else None
                extra = max(tz or 0, dtt or 0) if use_tz or use_dtt else None
                for k, expected in [('tz01Demand', tz), ('dtt3Demand', dtt), ('demandLevel', extra)]:
                    actual = row['platform'][k]
                    assert actual is None if expected is None else math.isclose(actual, expected, abs_tol=1e-10)
            else:
                extra = None; assert row['platform']['demandLevel'] is None
            complete = all(fresh(frame[k], now, 3) for k in ('CpuPackage','CpuCoreMax','Gpu')) and all(
                frame.get(k) is not None for k in ('CpuPowerW','GpuPowerW','CpuLoadPercent','GpuLoadPercent')) and now in cores
            assert row['inputAvailable'] == complete
            b, candidate, shadow = (row[k] for k in ('baseline','candidate','internalShadowDecision'))
            if b is not None and b['accepted']:
                counts['baseAccepted'] += 1
                core_values = cores[now]
                cpu_source = tuning['cpuTemperatureSource']
                raw_cpu = max(frame['CpuPackage']['Value'], frame['CpuCoreMax']['Value'])
                selected = [c['temperatureC'] for c in core_values if cpu_source == 1 or c['coreType'] == 'Performance']
                if cpu_source == 3: selected = sorted(selected, reverse=True)[:tuning['hottestPerformanceCoreCount']]
                cpu = raw_cpu if cpu_source == 0 else sum(selected)/len(selected)
                def scale(value, cold, hot): return min(100, max(0, (value-cold)/(hot-cold)*100))
                normalized = [scale(cpu,40,90),scale(frame['Gpu']['Value'],35,81),scale(frame['CpuPowerW'],0,60),
                              scale(frame['GpuPowerW'],0,75),frame['CpuLoadPercent'],frame['GpuLoadPercent']]
                gains = [demand[k+'Influence'] for k in ('cpuTemperature','gpuTemperature','cpuPower','gpuPower','cpuLoad','gpuLoad')]
                contributions = [min(100, max(0, v*g/100)) for v,g in zip(normalized,gains)]
                cpu_floor = 50 if raw_cpu >= 90 else 44 if raw_cpu >= 85 else 10
                gpu_floor = 50 if frame['Gpu']['Value'] >= 81 else 44 if frame['Gpu']['Value'] >= 78 else 10
                for index,floor in [(0,cpu_floor),(1,gpu_floor)]:
                    if floor > 10: contributions[index] = max(contributions[index],100 if floor == 50 else 90)
                raw = max(interpolate(demand['curve'],max(contributions),'input'),cpu_floor,gpu_floor)
                raw = min(tuning['maximumLevel'],max(tuning['minimumLevel'],raw))
                assert math.isclose(b['rawDemandLevel'],raw,abs_tol=1e-10), 'Existing six-input raw demand changed'
                if available:
                    if variant == 'both-retention': extra = min(extra, last_shadow if last_shadow is not None else b['equalFanLevel'])
                    remembered = extra
                    assert row['appliedSupplementalLevel'] == extra
                assert shadow is not None and shadow['accepted']
                if variant != 'baseline':
                    expected = min(tuning['maximumLevel'], max(tuning['minimumLevel'], b['rawDemandLevel'], remembered or 0))
                    assert math.isclose(shadow['rawDemandLevel'], expected, abs_tol=1e-10)
                assert shadow['thermalOverride'] == b['thermalOverride']
                last_shadow = shadow['equalFanLevel']
                assert row['rememberedSupplementalLevel'] == remembered
                assert (candidate is not None) == available
                if available: assert candidate == shadow, 'Proposed target differs from qualified observer'
            else:
                assert candidate is None and shadow is None
                remembered = None; last_shadow = None
            if candidate is not None and candidate['accepted']:
                assert available and b['accepted'] and row['proposedDisposition'] == 'SimulatedTarget'
                counts['accepted'] += 1; delta = candidate['equalFanLevel']-b['equalFanLevel']
                counts['raisedTargets'] += delta > 0; counts['targetsBelowBaseline'] += delta < 0
                counts['rawDemandBelowBaseline'] += candidate['rawDemandLevel'] < b['rawDemandLevel']
                counts['overrideMismatch'] += candidate['thermalOverride'] != b['thermalOverride']
                counts['maximumExtraLevels'] = max(counts['maximumExtraLevels'], delta)
                level = str(candidate['equalFanLevel']); histogram[level] = histogram.get(level, 0)+1
                if previous is not None:
                    interval = (now-previous[0])/10_000_000
                    if 0 < interval <= 3:
                        totals['pairedSeconds'] += interval
                        if delta > 0:
                            totals['extraSeconds'] += interval; totals['extraLevelSeconds'] += delta*interval
                            low = frame['CpuPowerW'] < 15 and frame['GpuPowerW'] < 10
                            assert row['lowPowerContext'] == low
                            totals['lowPowerExtraSeconds' if low else 'highPowerExtraSeconds'] += interval
                        counts['candidateChanges'] += previous[1] != candidate['equalFanLevel']
                        counts['baselineChanges'] += previous[2] != b['equalFanLevel']
                previous = (now, candidate['equalFanLevel'], b['equalFanLevel'])
            else:
                assert row['proposedDisposition'] == 'NoTargetHandoffRequired'
                counts['unavailable'] += 1; previous = None
            rows_checked += 1
        assert histogram == run['levelHistogram']
        for key, value in counts.items(): assert value == run[key], (session,profile,variant,key,value,run[key])
        for key, value in totals.items(): assert math.isclose(value, run[key], abs_tol=1e-7), (key,value,run[key])
        assert run['rawDemandBelowBaseline'] == run['overrideMismatch'] == run['targetsBelowBaseline'] == 0
    print(f'Independent platform reconciliation: PASS ({rows_checked} rows; {len(summary["runs"])} runs; original baseline unchanged).')
    if a.compress:
        for path in a.output.glob('*.jsonl'):
            data = path.read_bytes(); compressed = gzip.compress(data, mtime=0)
            assert gzip.decompress(compressed) == data
            pathlib.Path(str(path)+'.gz').write_bytes(compressed); path.unlink()

if __name__ == '__main__': main()
