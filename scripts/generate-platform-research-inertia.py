#!/usr/bin/env python3
"""Generate the research adapter from pinned production inertia without changing it."""
import argparse, hashlib, pathlib, re

EXPECTED_SOURCE_SHA256 = '263a0fe52359a349026185c461542e2b679d5768afbe902d6fac8f999a9a6c06'

def generate(source, output):
    text = source.read_text(encoding='utf-8-sig').replace('\r\n', '\n')
    if hashlib.sha256(text.encode()).hexdigest() != EXPECTED_SOURCE_SHA256:
        raise ValueError('Production inertia changed; review the research adapter and baseline evidence before regeneration.')
    marker = 'public class AdaptiveFanInertiaPolicy'
    if text.count(marker) != 1: raise ValueError('Production inertia class anchor changed')
    # Reuse the production decision/settings records; generate only the class.
    text = text.split('\n', 1)[0]+'\n\n'+text[text.index(marker):]
    text = re.sub(r'\bAdaptiveFanInertiaPolicy\b', 'ResearchFanInertiaPolicy', text)
    old = 'public AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input)\n    {'
    new = ('public AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input) => Evaluate(input, null);\n\n'
           '    internal AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input, double? supplementalDemandLevel)\n    {')
    if text.count(old) != 1: raise ValueError('Research entry anchor changed')
    text = text.replace(old, new)
    # Used only when the physical experiment switches variants. Keep the EMA/load
    # history, align the target to the last acknowledged physical request, and
    # discard pending confirmations from the preceding stage.
    anchor = '    public AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input) =>'
    transition = ('    internal void AlignExperimentalTarget(int level)\n    {\n'
                  '        if (level < _config.MinimumLevel || level > _config.MaximumLevel)\n'
                  '            throw new ArgumentOutOfRangeException(nameof(level));\n'
                  '        _current = level; ClearConfirmation();\n    }\n\n')
    text = text.replace(anchor, transition+anchor)
    anchor = 'var demand = _demand.Evaluate(input);'
    if text.count(anchor) != 2: raise ValueError('Production demand/observation anchors changed')
    text = text.replace(anchor, 'var demand = _demand.Evaluate(input, supplementalDemandLevel);', 1)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text('// Generated research adapter; production inertia source remains unchanged.\n'+text,
                      encoding='utf-8', newline='\n')

if __name__ == '__main__':
    p = argparse.ArgumentParser(); p.add_argument('source', type=pathlib.Path); p.add_argument('output', type=pathlib.Path)
    a = p.parse_args(); generate(a.source, a.output)
