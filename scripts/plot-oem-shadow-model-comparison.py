#!/usr/bin/env python3
"""Optional static research figure; requires matplotlib 3.10.8. No hardware IO."""
import argparse
import json
import math
import pathlib

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

p = argparse.ArgumentParser()
p.add_argument('comparison_directory', type=pathlib.Path)
p.add_argument('output_png', type=pathlib.Path)
a = p.parse_args()
report = json.loads((a.comparison_directory / 'comparison.json').read_text())
fold = next(f for f in report['folds'] if f['heldOut'] == 'live-20261008')
family = fold['primarySelectedFamilyFromInnerValidationOnly']
rows = [json.loads(s) for s in (a.comparison_directory / 'live-20261008-predictions.jsonl').read_text().splitlines()]
x = [(r['fanTicks'] - rows[0]['fanTicks']) / 600_000_000 for r in rows]
fig, axes = plt.subplots(2, 1, figsize=(12, 7), sharex=True, constrained_layout=True)
fig.suptitle('Sesión de una hora reservada para evaluación\nAjuste con v10 + v11 · modelo térmico con historial', fontsize=15)
for i, ax in enumerate(axes):
    ax.plot(x, [r['actual'][i] * 100 for r in rows], label='Lectura OEM', color='#172b4d', linewidth=1.7)
    ax.plot(x, [r['predictions'][family][i] * 100 for r in rows], label='Modelo con historial', color='#008c95', linewidth=1.5)
    ax.plot(x, [r['originalSeedPrediction'][i] * 100 if r['originalSeedPrediction'] else math.nan for r in rows],
            label='Modelo original: punto medio del rango', color='#d97828', linewidth=1.2, linestyle='--')
    ax.set_ylabel(('CPU' if i == 0 else 'GPU') + ' · RPM nominales')
    ax.grid(alpha=.2)
    ax.set_ylim(1600, 5100)
axes[0].legend(loc='upper left', fontsize=9)
axes[1].set_xlabel('Minutos desde la primera adquisición evaluable')
axes[1].set_xlim(0, 60)
axes[1].text(.5, -.24, '1768 adquisiciones distintas · entradas disponibles antes de cada adquisición · observación, sin control',
             transform=axes[1].transAxes, ha='center', fontsize=9, color='#44546a')
a.output_png.parent.mkdir(parents=True, exist_ok=True)
fig.savefig(a.output_png, dpi=160, bbox_inches='tight')
print(a.output_png)
