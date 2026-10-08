#!/usr/bin/env python3
"""Independent reconciliation of model prediction errors, support and causal targets."""
import argparse
import bisect
import gzip
import importlib.util
import json
import math
import pathlib
import statistics
import sys

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('prepare', pathlib.Path(__file__).with_name('prepare-oem-shadow-replay.py'))
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


def percentile(values, percent):
    values = sorted(values)
    pos = (len(values) - 1) * percent / 100
    low = int(pos)
    high = min(low + 1, len(values) - 1)
    return values[low] + (values[high] - values[low]) * (pos - low)


def metrics(rows, family, seed_subset=False, high=False):
    actual, predicted = [], []
    for r in rows:
        if seed_subset and r['originalSeedPrediction'] is None:
            continue
        if high and r['actual'][0] < 39 and r['actual'][1] < 35:
            continue
        p = r['originalSeedPrediction'] if family == 'seed' else r['predictions'][family]
        if p is None:
            continue
        actual.append(r['actual'])
        predicted.append(p)
    if not actual:
        return None
    errors = [[p[i] - a[i] for i in range(2)] for a, p in zip(actual, predicted)]
    absolute = [[abs(e) for e in pair] for pair in errors]
    under = [max(0., -e[0], -e[1]) for e in errors]
    n = len(errors)
    return {'samples': n, 'cpuMaeLevels': statistics.mean(a[0] for a in absolute),
            'gpuMaeLevels': statistics.mean(a[1] for a in absolute),
            'meanBothMaeLevels': statistics.mean(v for a in absolute for v in a),
            'meanBothSignedErrorLevels': statistics.mean(v for e in errors for v in e),
            'p95WorstFanAbsoluteErrorLevels': percentile([max(a) for a in absolute], 95),
            'bothWithin2LevelsFraction': sum(max(a) <= 2. for a in absolute) / n,
            'eitherFanUnderByAtLeast5LevelsFraction': sum(u >= 5. for u in under) / n,
            'eitherFanUnderByAtLeast10LevelsFraction': sum(u >= 10. for u in under) / n,
            'meanWorstFanUnderLevels': statistics.mean(under)}


def equal(a, b):
    if a is None or b is None:
        assert a is b
    else:
        assert a.keys() == b.keys()
        for k in a:
            assert math.isclose(a[k], b[k], rel_tol=1e-9, abs_tol=1e-9), (k, a[k], b[k])


def main():
    p = argparse.ArgumentParser()
    p.add_argument('fixtures', type=pathlib.Path)
    p.add_argument('output', type=pathlib.Path)
    a = p.parse_args()
    report = json.loads((a.output / 'comparison.json').read_text())
    assert report['goForFanControl'] is False and report['hardwareIo'] is False
    names = set(report['sessions'])
    for fold in report['folds']:
        name = fold['heldOut']
        assert set(fold['trainingSessions']) == names - {name}
        frames = [json.loads(s) for s in gzip.decompress((a.fixtures / (name + '.jsonl.gz')).read_bytes()).splitlines()]
        times = [prepare.timestamp(f['TimestampUtc']) for f in frames]
        by_epoch = {}
        for f in frames:
            if f.get('FanSampledAtUtc'):
                by_epoch.setdefault(prepare.timestamp(f['FanSampledAtUtc']), f)
        rows = [json.loads(s) for s in (a.output / (name + '-predictions.jsonl')).read_text().splitlines()]
        assert len(rows) == report['sessions'][name]['usableCausalTargets']
        assert len({r['fanTicks'] for r in rows}) == len(rows)
        for r in rows:
            assert r['featureTicks'] <= r['fanTicks']
            f = frames[bisect.bisect_right(times, r['fanTicks']) - 1]
            assert prepare.timestamp(f['TimestampUtc']) == r['featureTicks']
            assert r['actual'] == [by_epoch[r['fanTicks']]['ActualCpuLevel'], by_epoch[r['fanTicks']]['ActualGpuLevel']]
            for source in ['CpuPackage', 'CpuCoreMax', 'Gpu', 'Tz01', 'Dtt3', 'Dtt1', 'Dtt2']:
                s = f[source]
                epoch = prepare.timestamp(s['SampledAtUtc'])
                assert 0 <= r['featureTicks'] - epoch < 30_000_000
                assert 0 <= r['fanTicks'] - epoch < 30_000_000
                assert s['Value'] is not None and 0 <= s['Value'] <= 120
            assert r['features'][:4] == [max(f['CpuPackage']['Value'], f['CpuCoreMax']['Value']), f['Gpu']['Value'], f['Tz01']['Value'], f['Dtt3']['Value']]
            assert r['features'][20:22] == [f['Dtt1']['Value'], f['Dtt2']['Value']]
            assert all(math.isfinite(x) for x in r['features'])
            assert all(len(pair) == 2 and all(math.isfinite(v) and 0 <= v <= 100 for v in pair) for pair in r['predictions'].values())
        inner_best = {}
        for family, comparison in fold['families'].items():
            assert set(comparison['model']['trainingSessions']) == names - {name}
            equal(metrics(rows, family), comparison['all'])
            equal(metrics(rows, family, high=True), comparison['highFanRegime'])
            equal(metrics(rows, family, seed_subset=True), comparison['onOriginalSeedAvailableTargets'])
            best = min(comparison['innerTrials'], key=lambda t: t['macroScore'])
            for trial in comparison['innerTrials']:
                assert math.isclose(statistics.mean(trial['innerHeldOutScores']), trial['macroScore'], rel_tol=1e-12)
            assert best['parameters'] == comparison['parameters']
            inner_best[family] = best['macroScore']
        selected = min(inner_best, key=inner_best.get)
        assert selected == fold['selectedFamilyFromInnerValidationOnly']
        equal(metrics(rows, selected), fold['selectedTestMetrics'])
        equal(metrics(rows, selected, high=True), fold['selectedHighFanMetrics'])
        primary = min(report['experimentStages']['primaryFamilies'], key=lambda name: inner_best[name])
        assert primary == fold['primarySelectedFamilyFromInnerValidationOnly']
        equal(metrics(rows, primary), fold['primarySelectedTestMetrics'])
        equal(metrics(rows, 'seed'), fold['originalSeed']['metricsOnAvailablePredictions'])
        assert sum(r['originalSeedPrediction'] is not None for r in rows) == fold['originalSeed']['usablePredictions']
        print(name, 'PASS: independent causal targets, raw labels, error/underprediction metrics, strata and nested selection accounting')
    for family, macro in report['descriptiveFamilyMacroMetrics'].items():
        for key, value in macro.items():
            assert math.isclose(value, statistics.mean(f['families'][family]['all'][key] for f in report['folds']), rel_tol=1e-9, abs_tol=1e-9)
    for key, value in report['nestedSelectedMacroMetrics'].items():
        assert math.isclose(value, statistics.mean(f['selectedTestMetrics'][key] for f in report['folds']), rel_tol=1e-9, abs_tol=1e-9)
    for key, value in report['primaryNestedSelectedMacroMetrics'].items():
        assert math.isclose(value, statistics.mean(f['primarySelectedTestMetrics'][key] for f in report['folds']), rel_tol=1e-9, abs_tol=1e-9)


if __name__ == '__main__':
    main()
