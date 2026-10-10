"""Exit 0 for successful same-commit checks, 2 while pending, 1 on failure."""
import json
from pathlib import Path
import sys


def check(runs, head, branch="main"):
    waiting = []
    for name in ("oem-shadow", "wmi-fan-experiment", "cpu-rapl"):
        matches = [r for r in runs if r["name"] == name and r["head_sha"] == head
                   and r["event"] == "push" and r["head_branch"] == branch]
        if not matches:
            waiting.append(name)
            continue
        latest = max(matches, key=lambda r: r["id"])
        if latest["status"] != "completed":
            waiting.append(name)
        elif latest["conclusion"] != "success":
            print(f"Release blocked: {name} {latest['conclusion']}")
            return 1
    if waiting:
        print("Waiting for same-commit checks: " + ", ".join(waiting))
        return 2
    print("Same-commit OEM, WMI and CPU/GPU checks: PASS")
    return 0


if __name__ == "__main__":
    data = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    sys.exit(check(data["workflow_runs"], sys.argv[2], sys.argv[3] if len(sys.argv)>3 else "main"))
