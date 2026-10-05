#!/usr/bin/env python3
"""Run a non-Claude model as a small tool-using agent on OpenRouter.

  agent.py "<task>" --model z-ai/glm-5.3-flash [--cwd DIR] [--allow-write] [--allow-shell]
                     [--max-turns 20] [--system TEXT] [--json]

Tools (OpenAI function-calling format, via /chat/completions):
  list_dir, read_file            always on, confined to --cwd
  write_file                     only with --allow-write, confined to --cwd
  run_command                    only with --allow-shell; runs with cwd=--cwd, 60s timeout, NOT sandboxed
                                 (secret-named environment variables are removed, but it can still read any file you can)

Env: OPENROUTER_API_KEY. Stdlib only. The key is never printed.
"""
import argparse, json, os, subprocess, sys, time, urllib.error, urllib.request
from pathlib import Path

URL = "https://openrouter.ai/api/v1/chat/completions"
MAX_READ = 20_000   # chars returned per read_file / command output
SYSTEM = ("You are a coding agent working in a directory. Use the tools to inspect and change files, "
          "then reply with a short final answer. Paths are relative to the working directory. "
          "Do not guess file contents; read them.")


def tool_specs(allow_write, allow_shell):
    def fn(name, desc, props, req):
        return {"type": "function", "function": {"name": name, "description": desc,
                "parameters": {"type": "object", "properties": props, "required": req}}}
    s = {"type": "string"}
    t = [fn("list_dir", "List entries of a directory (default '.').", {"path": s}, []),
         fn("read_file", "Read a text file.", {"path": s}, ["path"])]
    if allow_write:
        t.append(fn("write_file", "Create or overwrite a text file.", {"path": s, "content": s}, ["path", "content"]))
    if allow_shell:
        t.append(fn("run_command", "Run a shell command in the working directory.", {"command": s}, ["command"]))
    return t


SECRET_WORDS = ("KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "AUTH")


def shell_env():
    """The environment for run_command: the parent's, minus variables whose names look like secrets, so a model that runs
    `env` cannot read OPENROUTER_API_KEY. Files the shell can reach (for example ~/.aws) are not protected."""
    return {k: v for k, v in os.environ.items() if not any(w in k.upper() for w in SECRET_WORDS)}


def resolve(root, rel):
    p = (root / (rel or ".")).resolve()
    if p != root and root not in p.parents:
        raise ValueError(f"path escapes working directory: {rel}")
    return p


def run_tool(name, args, root, allow_write, allow_shell):
    try:
        if name == "list_dir":
            p = resolve(root, args.get("path"))
            return "\n".join(sorted(e.name + ("/" if e.is_dir() else "") for e in p.iterdir())) or "(empty)"
        if name == "read_file":
            return resolve(root, args["path"]).read_text(errors="replace")[:MAX_READ]
        if name == "write_file" and allow_write:
            p = resolve(root, args["path"])
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(args["content"])
            return f"wrote {len(args['content'])} chars to {args['path']}"
        if name == "run_command" and allow_shell:
            r = subprocess.run(args["command"], shell=True, cwd=root, capture_output=True, text=True, timeout=60,
                               env=shell_env())
            return f"exit={r.returncode}\n{(r.stdout + r.stderr)[:MAX_READ]}"
        return f"error: unknown or disabled tool {name}"
    except subprocess.TimeoutExpired:
        return "error: command timed out after 60s"
    except Exception as e:  # tool errors go back to the model, not the user
        return f"error: {type(e).__name__}: {e}"


def chat(body, key, retries=3):
    data = json.dumps(body).encode()
    hdr = {"Content-Type": "application/json", "Authorization": f"Bearer {key}"}
    for i in range(retries + 1):
        try:
            with urllib.request.urlopen(urllib.request.Request(URL, data, hdr), timeout=180) as r:
                out = json.load(r)
        except urllib.error.HTTPError as e:
            if e.code in (429, 502, 503, 529) and i < retries:
                time.sleep(2 ** (i + 1)); continue
            raise SystemExit(f"HTTP {e.code}: {e.read()[:300].decode(errors='replace')}")
        if "choices" not in out:  # OpenRouter reports upstream failures as 200 + error body
            err = out.get("error", {})
            if err.get("code") in (429, 502, 503, 529) and i < retries:
                time.sleep(2 ** (i + 1)); continue
            raise SystemExit(f"upstream error: {json.dumps(err)[:300]}")
        return out


def run_agent(task, model, root, allow_write=False, allow_shell=False, max_turns=20, key=None, system=None):
    key = key or os.environ.get("OPENROUTER_API_KEY") or sys.exit("OPENROUTER_API_KEY is not set")
    tools = tool_specs(allow_write, allow_shell)
    msgs = [{"role": "system", "content": f"{system}\n\n{SYSTEM}" if system else SYSTEM}, {"role": "user", "content": task}]
    usage = {"prompt_tokens": 0, "completion_tokens": 0, "cost": 0.0}
    calls = []
    for turn in range(1, max_turns + 1):
        out = chat({"model": model, "messages": msgs, "tools": tools}, key)
        for k in usage:
            usage[k] += (out.get("usage") or {}).get(k) or 0
        m = out["choices"][0]["message"]
        msgs.append({k: v for k, v in m.items() if k in ("role", "content", "tool_calls") and v is not None}
                    | {"role": "assistant"})
        if not m.get("tool_calls"):
            return {"model": model, "turns": turn, "answer": m.get("content") or "", "tool_calls": calls,
                    "usage": usage, "stopped": "done"}
        for tc in m["tool_calls"]:
            name = tc["function"]["name"]
            try:
                args = json.loads(tc["function"].get("arguments") or "{}")
            except json.JSONDecodeError:
                args = {}
                result = "error: arguments were not valid JSON"
            else:
                result = run_tool(name, args, root, allow_write, allow_shell)
            calls.append({"tool": name, "args": args})
            msgs.append({"role": "tool", "tool_call_id": tc["id"], "content": result})
    return {"model": model, "turns": max_turns, "answer": "", "tool_calls": calls, "usage": usage,
            "stopped": "max_turns"}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("task")
    ap.add_argument("--model", required=True)
    ap.add_argument("--cwd", default=".")
    ap.add_argument("--allow-write", action="store_true")
    ap.add_argument("--allow-shell", action="store_true")
    ap.add_argument("--max-turns", type=int, default=20)
    ap.add_argument("--system", help="extra system prompt, e.g. the system_prompt from route.py classify")
    ap.add_argument("--json", action="store_true", help="print the full result as JSON")
    a = ap.parse_args()
    root = Path(a.cwd).resolve()
    if not root.is_dir():
        sys.exit(f"--cwd is not a directory: {root}")
    r = run_agent(a.task, a.model, root, a.allow_write, a.allow_shell, a.max_turns, system=a.system)
    if a.json:
        print(json.dumps(r))
    else:
        print(r["answer"] or f"(stopped: {r['stopped']})")
        print(f"\n[{r['model']}: {r['turns']} turns, {len(r['tool_calls'])} tool calls, "
              f"{r['usage']['prompt_tokens']}+{r['usage']['completion_tokens']} tokens, ${r['usage']['cost']:.5f}]",
              file=sys.stderr)
    return 0 if r["stopped"] == "done" else 1


if __name__ == "__main__":
    sys.exit(main())
