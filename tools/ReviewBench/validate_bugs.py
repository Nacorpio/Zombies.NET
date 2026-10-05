#!/usr/bin/env python3
"""Check that each planted bug is a real defect: applied alone, it must make at least one existing test fail.
  validate_bugs.py <worktree> [BUG_IDS|all] [--project tests/Zombies.Engine.Tests] [--filter-class "*ContextMenu*"]
Needs dotnet on PATH and a worktree at the commit the bugs were written for (see README). Reverts each bug afterwards.
The class filter must match every test class that covers the code: "*ContextMenuTests*" silently skips ContextMenuHostTests."""
import re, subprocess, sys
from pathlib import Path
HERE = Path(__file__).resolve().parent
a = sys.argv[1:]
wt = a[0]; ids = a[1] if len(a) > 1 and not a[1].startswith("--") else "all"
opt = lambda f, d: a[a.index(f) + 1] if f in a else d
project, flt = opt("--project", "tests/Zombies.Engine.Tests"), opt("--filter-class", "*ContextMenu*")
def tests():
    out = subprocess.run(["dotnet", "test", "--project", project, "-c", "Release", "--filter-class", flt], cwd=wt, capture_output=True, text=True).stdout
    out = re.sub(r"\x1b\[[0-9;]*m", "", out)
    g = lambda k: int((re.search(rf"^\s*{k}:\s*(\d+)", out, re.M) or [0, -1])[1])
    return g("total"), g("failed")
total, failed = tests(); print(f"baseline: {total} tests, {failed} failed")
if total <= 0 or failed != 0: raise SystemExit("baseline must be green and non-empty")
bad = []
for b in [x["id"] for x in __import__("json").load(open(HERE / "bugs.json"))]:
    if ids != "all" and b not in ids.split(","): continue
    subprocess.run([sys.executable, str(HERE / "apply_bugs.py"), wt, b], check=True, capture_output=True)
    t, f = tests(); subprocess.run(["git", "checkout", "-q", "--", "."], cwd=wt, check=True)
    print(f"{b}: {f} failed of {t}" + ("" if f > 0 else "   <-- NOT caught by any test"))
    if f <= 0: bad.append(b)
raise SystemExit(1 if bad else 0)
