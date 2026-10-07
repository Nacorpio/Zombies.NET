#!/usr/bin/env python3
"""Seeded-bug code review benchmark. Stdlib only.
  bench.py prepare <worktree> <clean_ref> <base_ref>   write inputs/ (seeded + clean) from a worktree with bugs applied
  bench.py run [--models a,b] [--cap USD] [--dry-run]  call the models via OpenRouter, score against bugs.json
  bench.py rescore <results_dir>                       recompute the table from saved raw outputs (no API calls)
Env: OPENROUTER_API_KEY (run)."""
import json, os, re, subprocess, sys, time, datetime, urllib.request, urllib.error
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
HERE = Path(__file__).resolve().parent
BASE = "https://openrouter.ai/api/v1"
MODELS = {  # id: (usd per Mtok in, out) from OpenRouter listing, 2026-10-05
    "anthropic/claude-haiku-4.5": (1.0, 5.0), "anthropic/claude-sonnet-5.5": (2.0, 10.0),
    "anthropic/claude-opus-5.5": (4.0, 20.0), "anthropic/claude-fable-5.1": (10.0, 50.0),
    "openai/gpt-5.6-sol": (2.0, 10.0), "google/gemini-3.8-flash": (0.75, 3.75)}
MAX_OUT = 12000
SYSTEM = ("You are a meticulous senior code reviewer. Review the change against the ticket's requirements. "
          "Report only defects you are confident about: bugs, logic errors, and violations of the stated requirements. "
          "Do not report style, naming, missing tests, or speculative concerns. For each defect give the file, the line number "
          "exactly as labelled on the left (the number after L), a severity (high, medium or low), and a one-sentence explanation. "
          'Reply with only a JSON array of objects with keys "file", "line", "severity", "description". Reply [] if you find no defects.')

def numbered_diff(cwd, args):
    out = subprocess.run(["git", "diff", "-U3", *args], cwd=cwd, capture_output=True, text=True, check=True).stdout
    res, n = [], 0
    for line in out.splitlines():
        if line.startswith("diff --git"):
            res.append(f"\n### {line.split(' b/')[-1]}"); continue
        m = re.match(r"@@ -\d+(?:,\d+)? \+(\d+)", line)
        if m: n = int(m.group(1)); res.append("@@"); continue
        if line.startswith(("---", "+++", "index ", "new file", "deleted file")): continue
        if line.startswith("+"): res.append(f"L{n:<4}+ {line[1:]}"); n += 1
        elif line.startswith("-"): continue
        elif line.startswith(" "): res.append(f"L{n:<4}  {line[1:]}"); n += 1
    return "\n".join(res)

def prepare(wt, clean_ref, base_ref):
    d = HERE / "inputs"; d.mkdir(exist_ok=True)
    spec = (HERE / "spec.md").read_text()
    wrap = lambda body: f"## Ticket and author's notes\n{spec}\n## Change under review (tests omitted; each line is labelled with its line number in the new file, + marks added lines)\n{body}\n"
    paths = ["src", "mods"]
    (d / "seeded.txt").write_text(wrap(numbered_diff(wt, [base_ref, "--", *paths])))
    (d / "clean.txt").write_text(wrap(numbered_diff(wt, [base_ref, clean_ref, "--", *paths])))
    for k in ("seeded", "clean"): print(k, len((d / f"{k}.txt").read_text()), "chars")

def post(model, user):
    body = {"model": model, "messages": [{"role": "system", "content": SYSTEM}, {"role": "user", "content": user}],
            "temperature": 0, "max_tokens": MAX_OUT}
    req = urllib.request.Request(f"{BASE}/chat/completions", json.dumps(body).encode(),
                                 {"Content-Type": "application/json", "Authorization": f"Bearer {os.environ['OPENROUTER_API_KEY']}"})
    t = time.time()
    for i in range(4):
        try:
            with urllib.request.urlopen(req, timeout=600) as r: return json.load(r), time.time() - t
        except urllib.error.HTTPError as e:
            if e.code in (429, 502, 503, 529) and i < 3: time.sleep(2 ** (i + 1)); continue
            return {"error": f"HTTP {e.code}: {e.read()[:300].decode(errors='replace')}"}, time.time() - t
        except Exception as e:
            return {"error": repr(e)}, time.time() - t

def parse(text):
    if not text: return None
    s = text.strip(); s = re.sub(r"^```(?:json)?|```$", "", s, flags=re.M).strip()
    i, j = s.find("["), s.rfind("]")
    try: v = json.loads(s[i:j + 1]); return [f for f in v if isinstance(f, dict)]
    except Exception: return None

def score(findings, bugs, tol=3):
    """Matches findings to planted bugs by smallest line distance (a bug may list alt_lines, e.g. where the symptom shows).
    The file name is ignored when all bugs are in one file, because models sometimes cite a wrong file name."""
    one_file = len({b["file"] for b in bugs}) == 1
    pairs = []
    for b in bugs:
        lines = [b["line"], *b.get("alt_lines", [])]
        for k, f in enumerate(findings):
            try: ln = int(re.sub(r"\D", "", str(f.get("line"))) or -999)
            except Exception: continue
            if not one_file and Path(b["file"]).name not in str(f.get("file", "")): continue
            d = min(abs(ln - x) for x in lines)
            if d <= tol: pairs.append((d, b["id"], k))
    matched, used = {}, set()
    for d, bid, k in sorted(pairs):
        if bid in matched or k in used: continue
        matched[bid] = k; used.add(k)
    return matched, [f for k, f in enumerate(findings) if k not in used]

def rescore(rdir):
    """Recompute the table from saved raw outputs, with the current scoring rules (no API calls)."""
    bugs = json.load(open(HERE / "bugs.json")); rdir = Path(rdir); summary = json.load(open(rdir / "summary.json")); out = {}
    print(f"{'model':<30}{'found':>7}{'extra':>7}{'clean':>7}  missed")
    for m in summary:
        load = lambda k: json.load(open(rdir / "raw" / f"{m.replace('/', '_')}.{k}.json"))
        get = lambda r: (r.get("choices") or [{}])[0].get("message", {}).get("content") or ""
        seeded = parse(get(load("seeded"))) or []; clean = parse(get(load("clean")))
        matched, extra = score(seeded, bugs)
        out[m] = {"found": sorted(matched), "missed": [b["id"] for b in bugs if b["id"] not in matched], "extra": extra, "clean": clean}
        print(f"{m:<30}{len(matched):>4}/{len(bugs)}{len(extra):>7}{(len(clean) if clean is not None else 'n/a'):>7}  {','.join(out[m]['missed'])}")
    (rdir / "rescored.json").write_text(json.dumps(out, indent=1))

def run(models, cap, dry):
    bugs = json.load(open(HERE / "bugs.json"))
    ins = {k: (HERE / "inputs" / f"{k}.txt").read_text() for k in ("seeded", "clean")}
    est = lambda m: sum((len(SYSTEM) + len(t)) / 3.2 * MODELS[m][0] / 1e6 + MAX_OUT * MODELS[m][1] / 1e6 for t in ins.values())
    tot = sum(est(m) for m in models)
    print(f"{len(models)} models x 2 inputs; worst-case cost ${tot:.2f} (cap ${cap:.2f}); input ~{len(ins['seeded'])/3.2:.0f} tokens each")
    for m in models: print(f"  {m:<32} worst case ${est(m):.2f}")
    if tot > cap: raise SystemExit("over cap; aborting")
    if dry: return
    out = HERE / "results" / datetime.datetime.now().strftime("%Y%m%d-%H%M%S"); (out / "raw").mkdir(parents=True)
    jobs = [(m, k) for m in models for k in ("seeded", "clean")]
    with ThreadPoolExecutor(6) as ex: res = list(ex.map(lambda j: post(j[0], ins[j[1]]), jobs))
    rows = {}
    for (m, k), (r, secs) in zip(jobs, res):
        (out / "raw" / f"{m.replace('/', '_')}.{k}.json").write_text(json.dumps(r, indent=1))
        msg = (r.get("choices") or [{}])[0].get("message", {}); content = msg.get("content") or ""
        f = parse(content); u = r.get("usage") or {}
        rows.setdefault(m, {})[k] = {"findings": f, "error": r.get("error"), "secs": round(secs), "cost": u.get("cost"),
                                     "in": u.get("prompt_tokens"), "out": u.get("completion_tokens"), "parsed": f is not None}
    summary = {}
    for m, d in rows.items():
        s = d["seeded"]; c = d["clean"]
        matched, extra = score(s["findings"] or [], bugs) if s["parsed"] else ({}, [])
        summary[m] = {"found": sorted(matched), "missed": [b["id"] for b in bugs if b["id"] not in matched],
                      "extra_on_seeded": extra, "findings_on_clean": c["findings"] if c["parsed"] else None,
                      "cost": round(sum(x["cost"] or 0 for x in d.values()), 4), "secs": [s["secs"], c["secs"]],
                      "errors": [x["error"] for x in d.values() if x["error"]], "unparsed": [k for k, x in d.items() if not x["parsed"]]}
    (out / "summary.json").write_text(json.dumps(summary, indent=1))
    print(f"\nresults in {out}")
    print(f"{'model':<30}{'found':>7}{'extra':>7}{'clean':>7}{'cost$':>8}  missed")
    for m, s in summary.items():
        print(f"{m:<30}{len(s['found']):>4}/{len(bugs)}{len(s['extra_on_seeded']):>7}"
              f"{(len(s['findings_on_clean']) if s['findings_on_clean'] is not None else -1):>7}{s['cost']:>8.3f}  {','.join(s['missed'])}"
              + (f"  ERR/UNPARSED {s['errors'] or s['unparsed']}" if s['errors'] or s['unparsed'] else ""))

if __name__ == "__main__":
    a = sys.argv[1:]
    if a and a[0] == "prepare": prepare(a[1], a[2], a[3])
    elif a and a[0] == "rescore": rescore(a[1])
    elif a and a[0] == "run":
        g = lambda f, d=None: a[a.index(f) + 1] if f in a else d
        run(g("--models", ",".join(MODELS)).split(","), float(g("--cap", "6")), "--dry-run" in a)
    else: raise SystemExit(__doc__)
