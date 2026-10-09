#!/usr/bin/env python3
"""Offline, nested leave-one-session-out comparison. Never accesses hardware."""
import argparse
import bisect
import collections
import gzip
import hashlib
import importlib.util
import json
import math
import pathlib
import sys

import numpy as np
from sklearn.tree import DecisionTreeRegressor

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('prepare', pathlib.Path(__file__).with_name('prepare-oem-shadow-replay.py'))
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)
VERSIONS = ('v10', 'v11', 'live-20261008')
SOURCES = ('CpuPackage', 'CpuCoreMax', 'Gpu', 'Tz01', 'Dtt3', 'Dtt1', 'Dtt2')
FEATURE_NAMES = ['cpu_max', 'gpu', 'tz01', 'dtt3']
FEATURE_NAMES += [f'{kind}_{name}' for kind in ('ema30', 'ema120', 'peak120', 'above_ema30')
                  for name in ('cpu_max', 'gpu', 'tz01', 'dtt3')]
FEATURE_NAMES += [f'{kind}_{name}' for kind in ('current', 'ema30', 'ema120', 'peak120', 'above_ema30')
                  for name in ('dtt1', 'dtt2')]
GRIDS = {
    'constant': [{}],
    'cpu_gpu_linear': [{'alpha': a} for a in (.001, .01, .1, 1.)],
    'thermal_linear': [{'alpha': a} for a in (.001, .01, .1, 1.)],
    'thermal_history_linear': [{'alpha': a} for a in (.001, .01, .1, 1.)],
    'thermal_tree': [{'depth': d, 'leaf': l} for d, l in ((3, 30), (5, 30), (5, 80))],
    'thermal_history_tree': [{'depth': d, 'leaf': l} for d, l in ((3, 30), (5, 30), (5, 80))],
}
PRIMARY_FAMILIES = tuple(GRIDS)
GRIDS.update({
    'context_thermal_linear': [{'alpha': a} for a in (.001, .01, .1, 1.)],
    'context_thermal_history_linear': [{'alpha': a} for a in (.001, .01, .1, 1.)],
    'context_thermal_tree': [{'depth': d, 'leaf': l} for d, l in ((3, 30), (5, 30), (5, 80))],
    'context_thermal_history_tree': [{'depth': d, 'leaf': l} for d, l in ((3, 30), (5, 30), (5, 80))],
})


def fresh(source, at):
    if not source or source.get('Value') is None or source.get('SampledAtUtc') is None:
        return False
    v = source['Value']
    return math.isfinite(v) and 0 <= v <= 120 and 0 <= at - prepare.timestamp(source['SampledAtUtc']) < 30_000_000


def dataset(name, frames, baseline=None):
    """Features must have been available BEFORE the fan acquisition, not its log row."""
    times, snapshots, rows = [], [], []
    history = collections.deque()
    last = None
    ema30 = ema120 = held = None
    for i, f in enumerate(frames):
        now = prepare.timestamp(f['TimestampUtc'])
        if times and now <= times[-1]:
            raise ValueError('Nonmonotonic frame timeline')
        times.append(now)
        if not all(fresh(f.get(s), now) for s in SOURCES):
            history.clear()
            last = ema30 = ema120 = held = None
            snapshots.append(None)
            continue
        current = np.array([max(f['CpuPackage']['Value'], f['CpuCoreMax']['Value']),
                            f['Gpu']['Value'], f['Tz01']['Value'], f['Dtt3']['Value'],
                            f['Dtt1']['Value'], f['Dtt2']['Value']], dtype=float)
        if last is None or now - last > 50_000_000:
            history.clear()
            ema30 = current.copy()
            ema120 = current.copy()
        else:
            dt = (now - last) / 10_000_000
            # Integrate the previously available value over the elapsed interval.
            ema30 = held + (ema30 - held) * math.exp(-dt / 30.)
            ema120 = held + (ema120 - held) * math.exp(-dt / 120.)
        history.append((now, current))
        while history and now - history[0][0] > 1_200_000_000:
            history.popleft()
        peak = np.max(np.stack([x[1] for x in history]), axis=0)
        blocks = (current, ema30, ema120, peak, current - ema30)
        x = np.concatenate(tuple(b[:4] for b in blocks) + tuple(b[4:] for b in blocks))
        snapshots.append(x)
        last, held = now, current

    seen = set()
    rejected = collections.Counter()
    eligible = 0
    last_fan = None
    for i, f in enumerate(frames):
        raw_epoch = f.get('FanSampledAtUtc')
        if raw_epoch is None:
            continue
        fan = prepare.timestamp(raw_epoch)
        if fan in seen:
            continue
        seen.add(fan)
        levels = [f.get('ActualCpuLevel'), f.get('ActualGpuLevel')]
        if any(type(v) is not int or not 0 <= v <= 100 for v in levels):
            rejected['invalidFanPair'] += 1
            continue
        if not 0 <= times[i] - fan < 30_000_000:
            rejected['staleOrFutureFan'] += 1
            continue
        if last_fan is not None and fan <= last_fan:
            rejected['regressingFanEpoch'] += 1
            continue
        last_fan = fan
        eligible += 1
        j = bisect.bisect_right(times, fan) - 1
        if j < 0 or snapshots[j] is None or fan - times[j] > 50_000_000:
            rejected['noCausalFreshSnapshot'] += 1
            continue
        if not all(fresh(frames[j].get(s), fan) for s in SOURCES):
            rejected['thermalExpiredBeforeFanAcquisition'] += 1
            continue
        seed = baseline[j]['prediction'] if baseline else None
        rows.append({'fanTicks': fan, 'featureTicks': times[j], 'x': snapshots[j],
                     'y': np.array(levels, dtype=float), 'seed': seed})
    if not rows:
        raise ValueError('No causal fresh targets in session: ' + name)
    return {'name': name, 'rows': rows, 'x': np.stack([r['x'] for r in rows]),
            'y': np.stack([r['y'] for r in rows]), 'coverage': {
                'eligibleFreshDistinctFanAcquisitions': eligible, 'usableCausalTargets': len(rows),
                'fraction': len(rows) / eligible if eligible else None, 'rejected': dict(rejected)}}


def columns(family):
    if family == 'constant':
        return []
    if family == 'cpu_gpu_linear':
        return [0, 1]
    if family.startswith('context_'):
        return list(range(30)) if 'history' in family else [0, 1, 2, 3, 20, 21]
    return list(range(20 if 'history' in family else 4))


def fit(family, parameters, training):
    x = np.concatenate([d['x'] for d in training])[:, columns(family)]
    y = np.concatenate([d['y'] for d in training])
    # Each complete training session has equal total weight; no random row split.
    weights = np.concatenate([np.full(len(d['y']), 1. / len(d['y']) / len(training)) for d in training])
    center_y = weights @ y
    model = {'family': family, 'parameters': parameters, 'trainingSessions': [d['name'] for d in training]}
    if family == 'constant':
        model['mean'] = center_y.tolist()
    elif 'tree' in family:
        tree = DecisionTreeRegressor(max_depth=parameters['depth'], min_samples_leaf=parameters['leaf'], random_state=0)
        tree.fit(x, y, sample_weight=weights)
        model['_tree'] = tree
        model['tree'] = {'childrenLeft': tree.tree_.children_left.tolist(), 'childrenRight': tree.tree_.children_right.tolist(),
                         'feature': tree.tree_.feature.tolist(), 'threshold': tree.tree_.threshold.tolist(),
                         'value': tree.tree_.value[:, :, 0].tolist()}
    else:
        center = weights @ x
        scale = np.sqrt(weights @ ((x - center) ** 2))
        scale[scale < 1e-8] = 1.
        z = (x - center) / scale
        coef = np.linalg.solve(z.T @ (weights[:, None] * z) + parameters['alpha'] * np.eye(z.shape[1]),
                               z.T @ (weights[:, None] * (y - center_y)))
        model.update(center=center.tolist(), scale=scale.tolist(), coefficients=coef.tolist(), intercept=center_y.tolist())
    return model


def predict(model, d):
    x = d['x'][:, columns(model['family'])]
    if model['family'] == 'constant':
        p = np.tile(model['mean'], (len(x), 1))
    elif '_tree' in model:
        p = model['_tree'].predict(x)
    else:
        p = ((x - model['center']) / model['scale']) @ np.array(model['coefficients']) + model['intercept']
    return np.clip(p, 0., 100.)


def metrics(y, p):
    if not len(y):
        return None
    error = p - y
    absolute = np.abs(error)
    under = np.max(np.maximum(-error, 0.), axis=1)
    return {'samples': len(y), 'cpuMaeLevels': float(np.mean(absolute[:, 0])), 'gpuMaeLevels': float(np.mean(absolute[:, 1])),
            'meanBothMaeLevels': float(np.mean(absolute)), 'meanBothSignedErrorLevels': float(np.mean(error)),
            'p95WorstFanAbsoluteErrorLevels': float(np.percentile(np.max(absolute, axis=1), 95)),
            'bothWithin2LevelsFraction': float(np.mean(np.all(absolute <= 2., axis=1))),
            'eitherFanUnderByAtLeast5LevelsFraction': float(np.mean(under >= 5.)),
            'eitherFanUnderByAtLeast10LevelsFraction': float(np.mean(under >= 10.)),
            'meanWorstFanUnderLevels': float(np.mean(under))}


def score(y, p):
    m = metrics(y, p)
    return m['meanBothMaeLevels'] + .5 * m['meanWorstFanUnderLevels']


def choose(training, family):
    trials = []
    for parameters in GRIDS[family]:
        scores = []
        for i, inner_test in enumerate(training):
            inner_train = [d for j, d in enumerate(training) if j != i]
            model = fit(family, parameters, inner_train)
            scores.append(score(inner_test['y'], predict(model, inner_test)))
        trials.append({'parameters': parameters, 'innerHeldOutScores': scores, 'macroScore': float(np.mean(scores))})
    best = min(trials, key=lambda t: t['macroScore'])
    return best, trials


def thermal_neighbors(training, held_out):
    """Descriptive ambiguity check, never a predictor or a selection criterion."""
    tx = np.concatenate([d['x'][:, :4] for d in training])
    ty = np.concatenate([d['y'] for d in training])
    refs = [(d['name'], r['fanTicks']) for d in training for r in d['rows']]
    result = []
    for tolerance in (.1, 2.):
        matched = differing = 0
        example = None
        for i, x in enumerate(held_out['x']):
            neighbors = np.flatnonzero(np.all(np.abs(tx - x[:4]) <= tolerance, axis=1))
            if not len(neighbors):
                continue
            matched += 1
            differences = np.max(np.abs(ty[neighbors] - held_out['y'][i]), axis=1)
            pos = int(np.argmax(differences))
            gap = float(differences[pos])
            if gap >= 5.:
                differing += 1
            if example is None or gap > example['worstFanDifferenceLevels']:
                j = int(neighbors[pos])
                example = {'worstFanDifferenceLevels': gap, 'heldOutFanTicks': held_out['rows'][i]['fanTicks'],
                           'heldOutThermal': x[:4].tolist(), 'heldOutFans': held_out['y'][i].tolist(),
                           'trainingSession': refs[j][0], 'trainingFanTicks': refs[j][1],
                           'trainingThermal': tx[j].tolist(), 'trainingFans': ty[j].tolist()}
        result.append({'eachInputToleranceC': tolerance, 'heldOutTargetsWithNeighbor': matched,
                       'targetsWithNeighborDifferentByAtLeast5Levels': differing, 'largestDifferenceExample': example})
    return {'inputs': FEATURE_NAMES[:4], 'comparisons': result,
            'interpretation': 'Similar current temperatures can coexist with different fan levels across sessions. This does not distinguish history, operating mode or other hidden inputs.'}


def evaluate(data):
    folds = []
    for held_out in data:
        training = [d for d in data if d['name'] != held_out['name']]
        comparisons = {}
        choices = {}
        for family in GRIDS:
            best, trials = choose(training, family)
            choices[family] = best
            model = fit(family, best['parameters'], training)
            p = predict(model, held_out)
            high = (held_out['y'][:, 0] >= 39) | (held_out['y'][:, 1] >= 35)
            comparisons[family] = {'parameters': best['parameters'], 'innerTrials': trials,
                                   'all': metrics(held_out['y'], p), 'highFanRegime': metrics(held_out['y'][high], p[high]),
                                   'model': {k: v for k, v in model.items() if k != '_tree'},
                                   '_predictions': p}
        selected = min(choices, key=lambda family: choices[family]['macroScore'])
        primary_selected = min(PRIMARY_FAMILIES, key=lambda family: choices[family]['macroScore'])
        seed_indices, seed_predictions = [], []
        for i, r in enumerate(held_out['rows']):
            s = r['seed']
            if s and s['CpuRange'] and s['GpuRange']:
                seed_indices.append(i)
                seed_predictions.append([(s[k]['Min'] + s[k]['Max']) / 2. for k in ('CpuRange', 'GpuRange')])
        for comparison in comparisons.values():
            comparison['onOriginalSeedAvailableTargets'] = metrics(held_out['y'][seed_indices], comparison['_predictions'][seed_indices]) if seed_indices else None
        seed = {'usablePredictions': len(seed_indices), 'sameCausalTargetCount': len(held_out['rows']),
                'metricsOnAvailablePredictions': metrics(held_out['y'][seed_indices], np.array(seed_predictions)) if seed_indices else None,
                'definition': 'Midpoints of original ranges at the same causal feature snapshot; Unknown abstains.'}
        train_y = np.concatenate([d['y'] for d in training])
        lo, hi = np.min(train_y, axis=0), np.max(train_y, axis=0)
        target_outside = np.any((held_out['y'] < lo) | (held_out['y'] > hi), axis=1)
        folds.append({'heldOut': held_out['name'], 'trainingSessions': [d['name'] for d in training],
                      'instantaneousThermalNeighborDiagnostic': thermal_neighbors(training, held_out),
                      'targetSupportDiagnostic': {'trainingFanMin': lo.tolist(), 'trainingFanMax': hi.tolist(),
                                                  'heldOutFanMin': np.min(held_out['y'], axis=0).tolist(),
                                                  'heldOutFanMax': np.max(held_out['y'], axis=0).tolist(),
                                                  'outsideTrainingFanRangeCount': int(np.sum(target_outside)),
                                                  'interpretation': 'Outcome support diagnostic only; never used to select, fit or predict.'},
                      'selectedFamilyFromInnerValidationOnly': selected, 'selectedTestMetrics': comparisons[selected]['all'],
                      'selectedHighFanMetrics': comparisons[selected]['highFanRegime'], 'families': comparisons, 'originalSeed': seed})
        folds[-1].update(primarySelectedFamilyFromInnerValidationOnly=primary_selected,
                         primarySelectedTestMetrics=comparisons[primary_selected]['all'])
    macro = {family: {key: float(np.mean([f['families'][family]['all'][key] for f in folds]))
                      for key in folds[0]['families'][family]['all'] if key != 'samples'} for family in GRIDS}
    selected_macro = {key: float(np.mean([f['selectedTestMetrics'][key] for f in folds]))
                      for key in folds[0]['selectedTestMetrics'] if key != 'samples'}
    primary_macro = {key: float(np.mean([f['primarySelectedTestMetrics'][key] for f in folds]))
                     for key in folds[0]['primarySelectedTestMetrics'] if key != 'samples'}
    return {'schemaVersion': 1, 'mode': 'OFFLINE_RESEARCH_ONLY', 'goForFanControl': False, 'hardwareIo': False,
            'featureNames': FEATURE_NAMES, 'sessions': {d['name']: d['coverage'] for d in data},
            'protocol': {'outerSplit': 'Leave one entire session out; train on the other two.',
                         'innerSplit': 'Each training session held out in turn; select family/hyperparameters without outer labels.',
                         'selectionScore': 'Equal-session mean of both-fan MAE + 0.5 * worst-fan underprediction mean, in fan levels.',
                         'highFanDefinition': 'Actual CPU level >=39 or GPU level >=35; unsupported stratum is null.',
                         'causality': 'Feature snapshot log time <= fan acquisition; all seven source epochs fresh at both snapshot and acquisition.',
                         'weighting': 'Equal total training weight per session; one target per distinct fresh fan acquisition.',
                         'labels': 'Raw fan pairs, including formerly unmapped levels. No A-D filter, no actual fan input, no load/power input.',
                         'limits': 'Three correlated sessions of one laptop; historic source epochs approximate acquisition. No proof of hidden OEM logic or control safety.'},
            'experimentStages': {'primaryFamilies': list(PRIMARY_FAMILIES),
                                 'exploratoryContextFamilies': [f for f in GRIDS if f not in PRIMARY_FAMILIES],
                                 'disclosure': 'DTT1/DTT2 families added after inspecting primary outer results. Their retrospective outer scores are exploratory; do not claim untouched validation. No hyperparameter grid was tuned to outer labels.'},
            'folds': folds, 'descriptiveFamilyMacroMetrics': macro, 'nestedSelectedMacroMetrics': selected_macro,
            'primaryNestedSelectedMacroMetrics': primary_macro}


def self_test():
    start = prepare.timestamp('2026-10-08T00:00:00.0000000Z')
    def stamp(s):
        return '2026-10-08T00:00:%02d.0000000Z' % s
    frames = []
    for i in range(40):
        f = {'TimestampUtc': stamp(i), 'FanSampledAtUtc': stamp(i), 'ActualCpuLevel': 22, 'ActualGpuLevel': 20}
        for s in SOURCES:
            f[s] = {'Value': 40 + i % 5, 'SampledAtUtc': stamp(i)}
        frames.append(f)
    original = dataset('test', frames)
    changed = json.loads(json.dumps(frames))
    for f in changed:
        f['ActualCpuLevel'] = 47
        f['ActualGpuLevel'] = 41
    assert np.array_equal(original['x'], dataset('test', changed)['x']), 'fan target leaked into features'
    changed = json.loads(json.dumps(frames))
    changed[20]['CpuPackage']['Value'] = 100
    altered = dataset('test', changed)
    assert np.array_equal(original['x'][:20], altered['x'][:20]), 'future thermal value changed past features'
    changed[20]['FanSampledAtUtc'] = stamp(19)  # cached native sample cannot create a new target
    assert len(dataset('test', changed)['y']) == 39
    future = json.loads(json.dumps(frames))
    future[20]['Dtt3']['SampledAtUtc'] = stamp(21)
    rejected = dataset('test', future)
    assert rejected['coverage']['usableCausalTargets'] == 39
    delayed = json.loads(json.dumps(frames))
    for i in range(1, 40):
        delayed[i]['FanSampledAtUtc'] = stamp(i - 1)
    causal = dataset('test', delayed)
    assert all(r['featureTicks'] <= r['fanTicks'] for r in causal['rows'])
    assert all(r['x'][0] == 40 + ((r['featureTicks'] - start) // 10_000_000) % 5 for r in causal['rows'])
    train_a = original
    train_b = dict(original, name='other')
    test = dict(original, name='held-out')
    best, trials = choose([train_a, train_b], 'thermal_history_linear')
    m = fit('thermal_history_linear', best['parameters'], [train_a, train_b])
    test['y'] = np.full_like(test['y'], 99.)
    best2, trials2 = choose([train_a, train_b], 'thermal_history_linear')
    assert best == best2 and trials == trials2
    assert m == fit('thermal_history_linear', best2['parameters'], [train_a, train_b]), 'held-out labels affected fitted model'
    pristine = dict(original, name='held-out')
    before = next(f for f in evaluate([train_a, train_b, pristine])['folds'] if f['heldOut'] == 'held-out')
    after = next(f for f in evaluate([train_a, train_b, test])['folds'] if f['heldOut'] == 'held-out')
    assert before['selectedFamilyFromInnerValidationOnly'] == after['selectedFamilyFromInnerValidationOnly']
    assert all(before['families'][family]['model'] == after['families'][family]['model'] for family in GRIDS), 'outer labels leaked into model or family selection'
    stale = json.loads(json.dumps(frames))
    stale[20]['Tz01']['SampledAtUtc'] = stamp(17)
    assert dataset('test', stale)['coverage']['usableCausalTargets'] == 39, 'exclusive freshness boundary'
    assert metrics(np.array([[47., 41.]]), np.array([[30., 25.]]))['eitherFanUnderByAtLeast10LevelsFraction'] == 1.
    context_missing = json.loads(json.dumps(frames))
    context_missing[20]['Dtt1'] = None
    assert dataset('test', context_missing)['coverage']['usableCausalTargets'] == 39
    gap = json.loads(json.dumps(frames[:20] + frames[30:]))
    for s in SOURCES:
        gap[19][s]['Value'] = 90
    resumed = dataset('test', gap)
    assert resumed['rows'][20]['x'][4:16].tolist() == [40.] * 12, 'gap incorrectly retains thermal history'
    expired = json.loads(json.dumps(frames))
    expired[19]['Tz01']['SampledAtUtc'] = '2026-10-08T00:00:16.5000000Z'
    expired[20]['FanSampledAtUtc'] = '2026-10-08T00:00:19.6000000Z'
    assert dataset('test', expired)['coverage']['rejected']['thermalExpiredBeforeFanAcquisition'] == 1
    try:
        dataset('test', frames + [frames[-1]])
    except ValueError:
        pass
    else:
        raise AssertionError('Nonmonotonic input accepted')
    print('OEM model comparison self-test: PASS (causal alignment, cached epochs, missing/future sources, fan independence, held-out isolation).')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--fixtures', type=pathlib.Path)
    parser.add_argument('--baseline-root', type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path)
    a = parser.parse_args()
    if a.self_test:
        self_test()
        return
    if not a.fixtures or not a.output:
        parser.error('--fixtures and --output required')
    if a.output.exists():
        parser.error('Output must be a new directory')
    data = []
    for version in VERSIONS:
        raw = (a.fixtures / (version + '.jsonl.gz')).read_bytes()
        manifest = json.loads((a.fixtures / (version + '.manifest.json')).read_text())
        plain = gzip.decompress(raw)
        assert hashlib.sha256(raw).hexdigest() == manifest['sha256']
        assert hashlib.sha256(plain).hexdigest() == manifest['uncompressedSha256']
        frames = [json.loads(s) for s in plain.splitlines()]
        assert len(frames) == manifest['samples']
        baseline = None
        if a.baseline_root:
            folder = a.baseline_root / ('oem-live' if version == 'live-20261008' else 'oem-' + version)
            baseline = [json.loads(s) for p in sorted(folder.glob('shadow-*.jsonl')) for s in p.read_text().splitlines()]
            assert len(baseline) == len(frames)
            def canonical(value):
                if isinstance(value, dict):
                    return {k: canonical(v) for k, v in value.items()}
                if isinstance(value, str) and 'T' in value and (value.endswith('Z') or '+' in value):
                    return prepare.timestamp(value)
                return value
            assert all(canonical(b['input']) == canonical(f) for b, f in zip(baseline, frames)), 'Baseline replay changed fixture inputs'
            fixed = json.loads((a.fixtures / 'prediction-baseline.json').read_text())['captures'][version]
            predictions = ('\n'.join(json.dumps(b['prediction'], sort_keys=True, separators=(',', ':')) for b in baseline) + '\n').encode()
            assert hashlib.sha256(predictions).hexdigest() == fixed['predictionSha256'], 'Not the frozen original predictor baseline'
        data.append(dataset(version, frames, baseline))
    result = evaluate(data)
    result['inputHashes'] = {v: json.loads((a.fixtures / (v + '.manifest.json')).read_text())['sha256'] for v in VERSIONS}
    result['versions'] = {'numpy': np.__version__, 'scikitLearn': __import__('sklearn').__version__}
    a.output.mkdir(parents=True)
    for d, fold in zip(data, result['folds']):
        with (a.output / (d['name'] + '-predictions.jsonl')).open('w') as out:
            for i, row in enumerate(d['rows']):
                out.write(json.dumps({'fanTicks': row['fanTicks'], 'featureTicks': row['featureTicks'], 'features': row['x'].tolist(), 'actual': row['y'].tolist(),
                                      'originalSeedPrediction': [(row['seed'][k]['Min'] + row['seed'][k]['Max']) / 2. for k in ('CpuRange', 'GpuRange')] if row['seed'] and row['seed']['CpuRange'] and row['seed']['GpuRange'] else None,
                                      'predictions': {family: item['_predictions'][i].tolist() for family, item in fold['families'].items()}}, allow_nan=False) + '\n')
        for item in fold['families'].values():
            del item['_predictions']
    (a.output / 'comparison.json').write_text(json.dumps(result, indent=2, allow_nan=False) + '\n')
    print('OEM model comparison: PASS; three nested session holdouts; report:', a.output / 'comparison.json')


if __name__ == '__main__':
    main()
