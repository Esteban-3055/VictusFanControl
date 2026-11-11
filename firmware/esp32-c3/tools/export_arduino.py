"""Generate an Arduino IDE sketch from the canonical sources; never maintain a second implementation."""
import argparse, pathlib, shutil
ROOT=pathlib.Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('destination')
p.add_argument('--enable-pwm',action='store_true',help='Enable the inverted GPIO3 output in the exported BoardConfig.h.')
args=p.parse_args()
target=pathlib.Path(args.destination).resolve()/'VictusBaseESP32C3';target.mkdir(parents=True,exist_ok=True)
(target/'VictusBaseESP32C3.ino').write_text('// Generated from firmware/esp32-c3. Implementation: Sketch.cpp\n',encoding='utf-8')
shutil.copyfile(ROOT/'src/main.cpp',target/'Sketch.cpp')
for file in sorted((ROOT/'include').glob('*.h')):
    if file.name != 'config.local.h':shutil.copyfile(file,target/file.name)
if args.enable_pwm:
    board=target/'BoardConfig.h'
    source=board.read_text(encoding='utf-8')
    assert source.count('#define VICTUS_ENABLE_PWM 0')==1
    board.write_text(source.replace('#define VICTUS_ENABLE_PWM 0','#define VICTUS_ENABLE_PWM 1'),encoding='utf-8')
for file in (ROOT/'lib/VictusProtocol').iterdir():
    if file.suffix in ('.h','.cpp'):shutil.copyfile(file,target/file.name)
shutil.copyfile(ROOT/'include/config.local.h.example',target/'config.local.h.example')
print(target)
