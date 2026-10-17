"""Generate an Arduino IDE sketch from the canonical sources; never maintain a second implementation."""
import argparse, pathlib, shutil
ROOT=pathlib.Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('destination');args=p.parse_args()
target=pathlib.Path(args.destination).resolve()/'VictusBaseESP32C3';target.mkdir(parents=True,exist_ok=True)
(target/'VictusBaseESP32C3.ino').write_text('// Generated from firmware/esp32-c3. Implementation: Sketch.cpp\n',encoding='utf-8')
shutil.copyfile(ROOT/'src/main.cpp',target/'Sketch.cpp')
shutil.copyfile(ROOT/'include/BoardConfig.h',target/'BoardConfig.h')
shutil.copyfile(ROOT/'include/LcdPresentation.h',target/'LcdPresentation.h')
for file in (ROOT/'lib/VictusProtocol').iterdir():
    if file.suffix in ('.h','.cpp'):shutil.copyfile(file,target/file.name)
shutil.copyfile(ROOT/'include/config.local.h.example',target/'config.local.h.example')
print(target)
