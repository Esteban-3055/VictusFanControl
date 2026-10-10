"""Verify an exact-build product artifact and prepare public release assets.

Runs without hardware IO or credentials. Publishing is a separate workflow step.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import zipfile


VERSION = "1.1.3"
TARGET = "HP-8C40-9D0R1LA-F18"
MANIFEST = "PRODUCT-GUI-MANIFEST.json"


def require(condition, detail):
    if not condition:
        raise ValueError(detail)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checks-branch", default="main")
    parser.add_argument("--verified-archive", type=Path, help="Publish original build ZIP after verifying every file; do not recompress on a different OS.")
    parser.add_argument("--payload", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-head", required=True)
    parser.add_argument("--build-run-id", type=int, required=True)
    parser.add_argument("--repository", default="Esteban-3055/VictusFanControl")
    parser.add_argument("--checks-json", type=Path)
    args = parser.parse_args()
    require(re.fullmatch(r"[0-9a-f]{40}", args.source_head), "Invalid source commit")
    require(args.build_run_id > 0, "Invalid build run")
    require(args.repository == "Esteban-3055/VictusFanControl", "Unexpected repository")
    root = args.payload.resolve(strict=True)
    require(not any(p.is_symlink() for p in root.rglob("*")), "Payload contains symlinks")
    manifest = json.loads((root / MANIFEST).read_text(encoding="utf-8-sig"))
    contract = json.loads((root / "PRODUCT-RELEASE.json").read_text(encoding="utf-8-sig"))
    require(manifest["kind"] == "VictusFanControl.ProductGuiRelease", "Wrong package kind")
    require(manifest["version"] == contract["version"] == VERSION, "Wrong version")
    require(manifest["sourceHead"] == contract["sourceHead"] == args.source_head,
            "Payload does not match the verified build commit")
    require(manifest["appDirectory"] == "app", "Wrong app layout")
    require(manifest["finalReleaseReady"] is True, "Package not ready")
    require(manifest["normalAutomatic"] == contract["normalAutomatic"] == "authorized-exact-target",
            "Wrong control authorization")
    require(contract["target"] == TARGET and contract["stableReleaseAuthorized"] is True,
            "Wrong target or missing release authorization")
    require(contract["physicalPassClaimed"] is False, "Must retain physical evidence limits")
    require(contract["experimentalPlatformRetention"]["defaultEnabled"] is False,
            "Retention must remain opt-in")
    required_observations = {
        "representative-use-of-95C-contract-and-stable-presets-ac-battery-idle-return",
        "current-gui-suspend-resume-without-fan-reentry",
        "current-gui-clean-exit-and-session-restart-release-and-open-in-firmware",
        "optional-quiet-ac-candidate-with-tz01-dtt3-comparable-thermal-and-measured-acoustics",
        "current-windows-logon-startup-coupled-activation-and-clean-shutdown",
        "configurable-protections-and-clean-unattended-resumption-on-current-target",
    }
    require(required_observations <= set(contract["remainingPhysicalChecks"]), "Missing pending observations")

    expected = {MANIFEST}
    folded = {MANIFEST.casefold()}
    for entry in manifest["files"]:
        name = entry["path"]
        path = PurePosixPath(name)
        require(isinstance(name, str) and not path.is_absolute() and ".." not in path.parts
                and "\\" not in name and ":" not in name and str(path) == name,
                "Unsafe payload path")
        require(name.casefold() not in folded, "Duplicate payload path")
        expected.add(name)
        folded.add(name.casefold())
        file = root / name
        require(not file.is_symlink() and file.resolve().is_relative_to(root) and file.is_file(),
                f"Missing or unsafe file: {name}")
        require(type(entry["size"]) is int and entry["size"] == file.stat().st_size,
                f"Size mismatch: {name}")
        require(hashlib.sha256(file.read_bytes()).hexdigest() == entry["sha256"],
                f"Hash mismatch: {name}")
    actual = {p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file()}
    require(actual == expected, "Unlisted/missing payload files")
    require({"Start-ProductGui.ps1", "Install-VictusFanControl.ps1", "PRODUCT_V1.md",
             "app/VictusFanControl.App.exe", "watchdog/VictusFanControl.Watchdog.exe",
             "app/performance-guardian/VictusFanControl.PerformanceGuardian.exe"} <= expected,
            "Incomplete application package")
    args.output.mkdir(parents=True, exist_ok=False)
    archive = args.output / f"VictusFanControl-{VERSION}-win-x64.zip"
    if args.verified_archive:
        require(args.verified_archive.name == archive.name, "Original archive filename mismatch")
        shutil.copyfile(args.verified_archive, archive)
    else:
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zip_file:
            for name in sorted(expected):
                info = zipfile.ZipInfo(name, date_time=(2026, 10, 9, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                zip_file.writestr(info, (root / name).read_bytes(), compresslevel=9)
    with zipfile.ZipFile(archive) as zip_file:
        require(zip_file.testzip() is None, "ZIP CRC failure")
        require(len(zip_file.namelist()) == len(expected) and set(zip_file.namelist()) == expected, "ZIP contents mismatch")
        for name in expected:
            require(zip_file.read(name) == (root / name).read_bytes(), f"ZIP changed {name}")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    (args.output / (archive.name + ".sha256")).write_text(
        f"{digest}  {archive.name}\n", encoding="utf-8")
    provenance = {
        "schemaVersion": 1, "version": VERSION, "target": TARGET,
        "repository": args.repository, "sourceHead": args.source_head,
        "buildRunId": args.build_run_id,
        "buildUrl": f"https://github.com/{args.repository}/actions/runs/{args.build_run_id}",
        "asset": archive.name, "size": archive.stat().st_size, "sha256": digest,
        "manifestSha256": hashlib.sha256((root / MANIFEST).read_bytes()).hexdigest(),
        "payloadFiles": len(expected), "physicalPassClaimed": False,
        "remainingPhysicalChecks": contract["remainingPhysicalChecks"],
        "evidenceLimits": contract["evidenceLimits"],
    }
    if args.checks_json:
        checks = json.loads(args.checks_json.read_text(encoding="utf-8"))["workflow_runs"]
        evidence = []
        for name in ("oem-shadow", "wmi-fan-experiment", "cpu-rapl"):
            matches = [r for r in checks if r["name"] == name and r["head_sha"] == args.source_head
                       and r["event"] == "push" and r["head_branch"] == args.checks_branch]
            require(matches, f"Missing same-commit check: {name}")
            latest = max(matches, key=lambda r: r["id"])
            require(latest["status"] == "completed" and latest["conclusion"] == "success",
                    f"Unsuccessful same-commit check: {name}")
            evidence.append({k: latest[k] for k in ("name", "id", "html_url", "head_sha", "conclusion")})
        provenance["sameCommitChecks"] = evidence
    (args.output / f"VictusFanControl-{VERSION}-provenance.json").write_text(
        json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print(f"Release assets verified: {len(expected)} files, SHA-256 {digest}")


if __name__ == "__main__":
    main()
