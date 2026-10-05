#!/usr/bin/env python3
"""Pick a model for a task: classify with Jev (TypeSafe System One) via OpenRouter, route in code.

  route.py classify "<task>"   print the routing decision as JSON
  route.py run "<task>"        classify, then call the chosen model on OpenRouter
  route.py decide              read Jev-style answers JSON on stdin, print decision (offline)
  route.py validate            check every model in tiers.json exists on OpenRouter

Env: OPENROUTER_API_KEY (classify/run). Stdlib only.
"""
import json, os, sys, time, urllib.error, urllib.request
from pathlib import Path

CFG_PATH = Path(__file__).resolve().parent.parent / "tiers.json"
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
        score = diff["score"] + (1 if stakes >= 0.5 else 0)
        tier = "fast" if score < 0.75 else "standard" if score < 1.75 else "hard" if score < 2.75 else "frontier"
        why = f"difficulty={diff['score']:.2f} high_stakes={stakes:.2f}"
    model = cfg["tiers"][tier]
    ov = cfg.get("kind_overrides", {}).get(kind["choice"])
    if ov and not low and tier in ("fast", "standard"):  # never downgrade hard work to a specialist
        model, why = ov, f"kind override ({kind['choice']}); " + why
    return {"tier": tier, "model": model, "kind": kind["choice"], "reason": why}


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
    resp = http(JEV_URL, {"state": task, "model": JEV_MODEL, "questions": QUESTIONS}, need("OPENROUTER_API_KEY"))
    return decide(resp["answers"], cfg)


def main(argv):
    if len(argv) < 2 or argv[1] not in ("classify", "run", "decide", "validate"):
        raise SystemExit(__doc__)
    cmd, cfg = argv[1], load_cfg()
    if cmd == "validate":
        ids = {m["id"] for m in http(f"{OR_BASE}/models")["data"]}
        want = list(cfg["tiers"].values()) + list(cfg.get("kind_overrides", {}).values())
        bad = [m for m in want if m not in ids]
        print(json.dumps({"checked": len(want), "missing": bad}))
        return 1 if bad else 0
    if cmd == "decide":
        print(json.dumps(decide(json.load(sys.stdin), cfg)))
        return 0
    task = " ".join(argv[2:]) or sys.stdin.read()
    d = classify(task, cfg)
    if cmd == "classify":
        print(json.dumps(d))
        return 0
    out = http(f"{OR_BASE}/chat/completions",
               {"model": d["model"], "messages": [{"role": "user", "content": task}]}, need("OPENROUTER_API_KEY"))
    print(json.dumps({"decision": d, "answer": out["choices"][0]["message"]["content"]}))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
