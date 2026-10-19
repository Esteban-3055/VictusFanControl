"""Actual authenticated parser -> source ages, plus reproducible Arduino export."""
import argparse
import json
import pathlib
import subprocess
import sys
import tempfile
from test_protocol import hello, auth, frame, telemetry, RX


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('host')
    args = parser.parse_args()
    payload = telemetry(False)
    payload.update(cpuTempSampledAtUtc='2026-10-16T16:59:59.1234567Z',
                   ramSampledAtUtc=None, decisionAtUtc='2026-10-16T16:59:56.1234567Z')
    messages = [hello(), auth(), frame('telemetry', 1, payload, RX)]
    result = subprocess.run([args.host, '--readings'], input='\n'.join(messages)+'\n',
                            text=True, capture_output=True, check=True)
    # At +1s residence: CPU thermal age=2s, GPU thermal fresh, demand age=5s,
    # RAM timestamp absent. MonitorOnly must not bypass source freshness.
    readings = json.loads(result.stderr.splitlines()[-1])
    assert readings == dict(cpuTemp=55, gpuTemp=42, cpu=10, gpu=0, ram=None, demand=None), readings
    payload.update(cpuTempC=None, gpuTempC=None, cpuUsagePct=None, gpuUsagePct=None,
                   ramUsagePct=None, demandaPct=None)
    messages[-1] = frame('telemetry', 1, payload, RX)
    result = subprocess.run([args.host, '--readings'], input='\n'.join(messages)+'\n',
                            text=True, capture_output=True, check=True)
    assert all(value is None for value in json.loads(result.stderr.splitlines()[-1]).values())
    print('PASS signed source ages, missing timestamps, zero and null readings')
    root = pathlib.Path(__file__).resolve().parents[1]
    with tempfile.TemporaryDirectory() as folder:
        subprocess.run([sys.executable, str(root/'tools/export_arduino.py'), folder], check=True)
        target = pathlib.Path(folder)/'VictusBaseESP32C3'
        for file in (root/'include').glob('*.h'):
            if file.name != 'config.local.h':
                assert (target/file.name).read_bytes() == file.read_bytes(), file.name
        for file in (root/'lib/VictusProtocol').iterdir():
            if file.suffix in ('.h', '.cpp'):
                assert (target/file.name).read_bytes() == file.read_bytes(), file.name
        assert (target/'Sketch.cpp').read_bytes() == (root/'src/main.cpp').read_bytes()
        assert not (target/'config.local.h').exists()
        subprocess.run([sys.executable, str(root/'tools/export_arduino.py'), folder, '--enable-pwm'], check=True)
        expected=(root/'include/BoardConfig.h').read_text(encoding='utf-8').replace(
            '#define VICTUS_ENABLE_PWM 0','#define VICTUS_ENABLE_PWM 1')
        assert (target/'BoardConfig.h').read_text(encoding='utf-8')==expected
        assert (target/'Sketch.cpp').read_bytes()==(root/'src/main.cpp').read_bytes()
        assert not (target/'config.local.h').exists()
    print('PASS Arduino export uses canonical headers and source without private config')
    print('PASS explicit PWM export changes only the GPIO3 enable default; no private configuration')


if __name__ == '__main__':
    main()
