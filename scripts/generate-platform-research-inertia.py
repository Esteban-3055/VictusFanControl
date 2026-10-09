#!/usr/bin/env python3
"""Generate the research adapter from pinned production inertia with a reviewed shared product entry."""
import argparse, hashlib, pathlib, re

EXPECTED_SOURCE_SHA256 = '3bbd2426fc4c23f46ef8b0db80c057de94e8296c72cfad0d83e55ec51abe03fe'

def generate(source, output):
    text = source.read_text(encoding='utf-8-sig').replace('\r\n', '\n')
    if hashlib.sha256(text.encode()).hexdigest() != EXPECTED_SOURCE_SHA256:
        raise ValueError('Production inertia changed; review the research adapter and baseline evidence before regeneration.')
    marker = 'public class AdaptiveFanInertiaPolicy'
    if text.count(marker) != 1: raise ValueError('Production inertia class anchor changed')
    # Reuse the production decision/settings records; generate only the class.
    text = text.split('\n', 1)[0]+'\n\n'+text[text.index(marker):]
    text = re.sub(r'\bAdaptiveFanInertiaPolicy\b', 'ResearchFanInertiaPolicy', text)
    # Research scalar floors use the same single-filter callback entry.
    anchor = '    internal AdaptiveFanInertiaDecision EvaluateWithSupplement(AdaptiveFanPolicyInput input, Func<double, double?>? supplementalDemand, bool retentionOnly = false)'
    if text.count(anchor) != 1: raise ValueError('Shared entry anchor changed')
    overload = ('    internal AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input, double? supplementalDemandLevel) =>\n'
                '        EvaluateWithSupplement(input, supplementalDemandLevel.HasValue ? _ => supplementalDemandLevel : null);\n\n')
    text = text.replace(anchor, overload+anchor)
    # Used only when the physical experiment switches variants. Keep the EMA/load
    # history, align the target to the last acknowledged physical request, and
    # discard pending confirmations from the preceding stage.
    anchor = '    public AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input) =>'
    transition = ('    internal void AlignExperimentalTarget(int level)\n    {\n'
                  '        if (level < _config.MinimumLevel || level > _config.MaximumLevel)\n'
                  '            throw new ArgumentOutOfRangeException(nameof(level));\n'
                  '        _current = level; ClearConfirmation();\n    }\n\n')
    text = text.replace(anchor, transition+anchor)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text('// Generated research adapter; shared production inertia; only stage alignment is research-specific.\n'+text,
                      encoding='utf-8', newline='\n')

if __name__ == '__main__':
    p = argparse.ArgumentParser(); p.add_argument('source', type=pathlib.Path); p.add_argument('output', type=pathlib.Path)
    a = p.parse_args(); generate(a.source, a.output)
