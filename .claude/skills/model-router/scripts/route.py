#!/usr/bin/env python3
"""Pick a model for a task: classify with Jev (TypeSafe System One) via OpenRouter, route in code.

  route.py classify [--log LABEL] "<task>"   print the routing decision as JSON
  route.py run [--log LABEL] "<task>"        classify, then call the chosen model on OpenRouter
  route.py decide                            read Jev-style answers JSON on stdin, print decision (offline)
  route.py validate                          check every model in tiers.json exists on OpenRouter
  route.py outcome LABEL key=value ...       record how a logged task went (result, fit, duration_s, tokens, tool_uses, note)
  route.py report                            show the calibration log and replay it against candidate thresholds (offline)

--log appends the raw Jev answers and the decision to calibration/log.jsonl (never the task text), so thresholds
can be re-evaluated later without calling Jev again. Set MODEL_ROUTER_LOG to use another file.

Env: OPENROUTER_API_KEY (classify/run). Stdlib only.
"""
import copy, datetime, json, os, sys, time, urllib.error, urllib.request
from pathlib import Path

CFG_PATH = Path(__file__).resolve().parent.parent / "tiers.json"
LOG_PATH = Path(os.environ.get("MODEL_ROUTER_LOG") or CFG_PATH.parent / "calibration" / "log.jsonl")
TIER_ORDER = ["fast", "standard", "hard", "frontier"]
OUTCOME_VALUES = {"result": {"ok", "rework", "failed"}, "fit": {"under", "right", "over"}}
OUTCOME_NUMBERS = {"duration_s", "tokens", "tool_uses"}
OR_BASE = "https://openrouter.ai/api/v1"
JEV_URL = f"{OR_BASE}/systemone"  # same body as TypeSafe's /v1/systemone
JEV_MODEL = "typesafe/jev-1.13"

# Questions are atomic (Jev reads literally; combine in code, not in the prompt).
QUESTIONS = {
    "kind": {
        "type": "choice",
        "instructions": "The dominant kind of work this task asks for",
        "criteria": {
            "code": "Writing, editing, debugging or reviewing source code",
            "reasoning": "Analysis, design, planning or multi-step problem solving without mainly writing code",
            "bulk_extraction": "Mechanical extraction, classification, reformatting or summarizing of many items",
            "web_research": "Needs fresh information from the web, with citations",
            "simple_qa": "A short factual question, lookup, rename or trivial edit",
        },
    },
    "difficulty": {
        "type": "score",
        "instructions": "How hard this task is for a language model to do correctly",
        "criteria": [
            "Trivial; one obvious step",
            "Routine; a few steps in a familiar pattern",
            "Demanding; needs careful multi-step reasoning or touches many parts",
            "Very hard; novel, ambiguous or deep expertise required",
        ],
    },
    "high_stakes": {
        "type": "noul",
        "instructions": "A mistake would be costly or hard to undo (security, data loss, production, money, legal)",
    },
}


def load_cfg():
    return json.loads(CFG_PATH.read_text())


def decide(answers, cfg):
    """Pure routing rule: Jev answers -> tier/model. Low confidence falls back to default."""
    kind = answers["kind"]
    diff = answers["difficulty"]
    stakes = answers["high_stakes"].get("noul", 0.0)
    low = min(kind.get("confidence", 1), diff.get("confidence", 1)) < cfg["min_confidence"]
    if low:
        tier, why = cfg["default_tier"], "low confidence; default tier"
    else:
        score = diff["score"] + (cfg.get("stakes_bump", 1) if stakes >= 0.5 else 0)
        c = cfg.get("cutoffs", [0.75, 1.75, 2.75])
        tier = TIER_ORDER[sum(score >= x for x in c)]
        why = f"difficulty={diff['score']:.2f} high_stakes={stakes:.2f}"
    model = cfg["tiers"][tier]
    ov = cfg.get("kind_overrides", {}).get(kind["choice"])
    if ov and not low and tier in ("fast", "standard"):  # never downgrade hard work to a specialist
        model, why = ov, f"kind override ({kind['choice']}); " + why
    sp = cfg.get("system_prompts", {})
    # kind prompt only when Jev was confident about the kind; the addendum follows the stakes answer alone
    kind_ok = kind.get("confidence", 1) >= cfg["min_confidence"]
    prompt = sp.get("kinds", {}).get(kind["choice"]) if kind_ok else None
    prompt = prompt or sp.get("default", "")
    if stakes >= 0.5 and sp.get("high_stakes_addendum"):
        prompt = f"{prompt}\n\n{sp['high_stakes_addendum']}".strip()
    alts = cfg.get("alternatives", {})
    alt = [m for m in alts.get("kind", {}).get(kind["choice"], []) + alts.get("tier", {}).get(tier, []) if m != model]
    return {"tier": tier, "model": model, "kind": kind["choice"], "reason": why,
            "system_prompt": prompt, "alternatives": list(dict.fromkeys(alt))}


def http(url, body=None, key=None, retries=3):
    data = json.dumps(body).encode() if body is not None else None
    hdr = {"Content-Type": "application/json"}
    if key:
        hdr["Authorization"] = f"Bearer {key}"
    for i in range(retries + 1):
        try:
            req = urllib.request.Request(url, data, hdr)
            with urllib.request.urlopen(req, timeout=120) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            if e.code in (429, 529) and i < retries:
                time.sleep(2 ** (i + 1))
                continue
            raise SystemExit(f"HTTP {e.code} from {url}: {e.read()[:300].decode(errors='replace')}")


def need(name):
    v = os.environ.get(name)
    if not v:
        raise SystemExit(f"{name} is not set")
    return v


def classify(task, cfg):
    """Returns (decision, answers) so the caller can log the raw answers."""
    resp = http(JEV_URL, {"state": task, "model": JEV_MODEL, "questions": QUESTIONS}, need("OPENROUTER_API_KEY"))
    return decide(resp["answers"], cfg), resp["answers"]


# --- calibration log -------------------------------------------------------------------------

def read_log():
    if not LOG_PATH.exists():
        return []
    return [json.loads(line) for line in LOG_PATH.read_text().splitlines() if line.strip()]


def write_log(records):
    LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
    LOG_PATH.write_text("".join(json.dumps(r, separators=(",", ":")) + "\n" for r in records))


def compact_answers(a):
    """The subset decide() reads. Task text is deliberately not stored."""
    return {"kind": {"choice": a["kind"]["choice"], "confidence": a["kind"].get("confidence")},
            "difficulty": {"score": a["difficulty"]["score"], "confidence": a["difficulty"].get("confidence")},
            "high_stakes": {"noul": a["high_stakes"].get("noul")}}


def log_decision(label, answers, d, cfg, task_chars):
    rec = {"label": label, "ts": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
           "task_chars": task_chars, "answers": compact_answers(answers),
           "decision": {k: d[k] for k in ("tier", "model", "kind", "reason")},
           "config": {k: cfg.get(k) for k in ("min_confidence", "cutoffs", "stakes_bump")}, "outcome": {}}
    write_log(read_log() + [rec])


def set_outcome(label, pairs):
    recs = read_log()
    rec = next((r for r in reversed(recs) if r["label"] == label), None)
    if rec is None:
        raise SystemExit(f"no log entry labelled {label!r}")
    for p in pairs:
        k, _, v = p.partition("=")
        if k in OUTCOME_VALUES and v not in OUTCOME_VALUES[k]:
            raise SystemExit(f"{k} must be one of {sorted(OUTCOME_VALUES[k])}")
        if k in OUTCOME_NUMBERS:
            v = float(v) if "." in v else int(v)
        elif k not in OUTCOME_VALUES and k != "note":
            raise SystemExit(f"unknown outcome key {k!r}")
        rec["outcome"][k] = v
    write_log(recs)


def wanted_tier(rec):
    """The tier the human's `fit` verdict implies: under = needed a stronger model, over = a weaker one would do."""
    fit = rec["outcome"].get("fit")
    if fit is None:
        return None
    i = TIER_ORDER.index(rec["decision"]["tier"]) + {"under": 1, "over": -1, "right": 0}[fit]
    return TIER_ORDER[max(0, min(len(TIER_ORDER) - 1, i))]


def report(cfg):
    recs = read_log()
    if not recs:
        print("log is empty")
        return
    print(f"{'label':<8}{'tier':<9}{'diff':>5}{'conf':>6}{'stakes':>7}  {'fit':<6}{'result':<8}{'secs':>6}{'tokens':>8}")
    for r in recs:
        a, o = r["answers"], r["outcome"]
        d = a["difficulty"] if a else {}
        f = lambda x, w, p=2: (f"{x:{w}.{p}f}" if isinstance(x, (int, float)) else " " * (w - 1) + "-")
        print(f"{r['label']:<8}{r['decision']['tier']:<9}{f(d.get('score'), 5)}{f(d.get('confidence'), 6)}"
              f"{f(a['high_stakes']['noul'] if a else None, 7)}  {o.get('fit', '-'):<6}{o.get('result', '-'):<8}"
              f"{o.get('duration_s', '-'):>6}{o.get('tokens', '-'):>8}")
    usable = [r for r in recs if r["answers"] and wanted_tier(r)]
    print(f"\n{len(recs)} entries, {sum(1 for r in recs if r['answers'])} with raw answers, "
          f"{len(usable)} with a fit verdict (replayable).")
    if len(usable) < 20:
        print("Too few for a trustworthy threshold change (the cookbook calibrates on 100-200 items). Read the sweeps as hints only.")
    if not usable:
        return

    def hits(c):
        return sum(decide(r["answers"], c)["tier"] == wanted_tier(r) for r in usable)

    base_c = cfg.get("cutoffs", [0.75, 1.75, 2.75])
    print(f"\nreplay at current settings: {hits(cfg)}/{len(usable)} match the fit verdict")
    print("cutoff shift (added to all three cutoffs):")
    for off in (-0.75, -0.5, -0.25, 0, 0.25, 0.5, 0.75):
        c = copy.deepcopy(cfg)
        c["cutoffs"] = [x + off for x in base_c]
        print(f"  {off:+.2f}  cutoffs={[round(x, 2) for x in c['cutoffs']]}  {hits(c)}/{len(usable)}")
    print("min_confidence:")
    for mc in (0.3, 0.4, 0.5, 0.6, 0.7):
        c = copy.deepcopy(cfg)
        c["min_confidence"] = mc
        print(f"  {mc:.1f}  {hits(c)}/{len(usable)}")


def main(argv):
    if len(argv) < 2 or argv[1] not in ("classify", "run", "decide", "validate", "outcome", "report"):
        raise SystemExit(__doc__)
    cmd, cfg = argv[1], load_cfg()
    if cmd == "outcome":
        if len(argv) < 4:
            raise SystemExit(__doc__)
        set_outcome(argv[2], argv[3:])
        return 0
    if cmd == "report":
        report(cfg)
        return 0
    label = None
    if "--log" in argv:
        i = argv.index("--log")
        label = argv[i + 1] if i + 1 < len(argv) else None
        if not label:
            raise SystemExit("--log needs a label")
        argv = argv[:i] + argv[i + 2:]
    if cmd == "validate":
        ids = {m["id"] for m in http(f"{OR_BASE}/models")["data"]}
        alts = cfg.get("alternatives", {})
        want = list(cfg["tiers"].values()) + list(cfg.get("kind_overrides", {}).values())
        want += [m for group in alts.values() for ms in group.values() for m in ms]
        bad = [m for m in want if m not in ids]
        print(json.dumps({"checked": len(want), "missing": bad}))
        return 1 if bad else 0
    if cmd == "decide":
        print(json.dumps(decide(json.load(sys.stdin), cfg)))
        return 0
    task = " ".join(argv[2:]) or sys.stdin.read()
    d, answers = classify(task, cfg)
    if label:
        log_decision(label, answers, d, cfg, len(task))
    if cmd == "classify":
        print(json.dumps(d))
        return 0
    msgs = ([{"role": "system", "content": d["system_prompt"]}] if d["system_prompt"] else []) + [{"role": "user", "content": task}]
    out = http(f"{OR_BASE}/chat/completions", {"model": d["model"], "messages": msgs}, need("OPENROUTER_API_KEY"))
    print(json.dumps({"decision": d, "answer": out["choices"][0]["message"]["content"]}))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
