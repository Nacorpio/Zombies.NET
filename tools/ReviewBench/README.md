# ReviewBench

A seeded-bug benchmark for comparing models at code review. It plants known bugs in a copy of a PR's code, has each model review the diff through OpenRouter, and scores how many planted bugs each model finds. A second review of the unmodified diff shows what each model reports when nothing is wrong.

Stdlib Python 3. Not part of the game build. Needs `OPENROUTER_API_KEY` for `run`; everything else is offline (`validate_bugs.py` needs `dotnet`).

The bugs in `bugs.json` are written for **PR #137's head commit `385bfee`** (branch `claude/ticket-50-context-menu`, `src/Zombies.Engine.Ui/ContextMenu.cs`). They only apply to that exact file content. If that commit is no longer reachable, write new bugs for another PR.

## Reproduce

```bash
git worktree add --detach /tmp/rb 385bfee
python3 tools/ReviewBench/validate_bugs.py /tmp/rb              # each bug alone must fail >= 1 of the 45 tests
python3 tools/ReviewBench/apply_bugs.py /tmp/rb all             # plant all bugs in the worktree
python3 tools/ReviewBench/bench.py prepare /tmp/rb 385bfee origin/main   # write inputs/seeded.txt and inputs/clean.txt
python3 tools/ReviewBench/bench.py run --dry-run                # worst-case cost, no calls
python3 tools/ReviewBench/bench.py run --cap 6                  # call the models; writes results/<timestamp>/
python3 tools/ReviewBench/bench.py rescore results/<timestamp>  # recompute the table from saved raw outputs
git -C /tmp/rb checkout -- .                                    # remove the planted bugs again
```

`run` refuses to start if the worst-case cost (every call using its full output budget) is over `--cap`. The first run cost about $0.76 in total.

## Notes

- Tests are left out of the review input, and the ticket text plus the PR's author notes are included.
- Scoring matches findings to bugs by smallest line distance (tolerance 3 lines). A bug may list `alt_lines` where its symptom shows. The file name is ignored when all bugs are in one file.
- `validate_bugs.py` needs a class filter that matches every test class covering the code. `*ContextMenuTests*` silently skips `ContextMenuHostTests`, which is why the default is `*ContextMenu*`.
- One run per model, temperature 0, 12,000 output tokens max. A reasoning model can spend that budget on thinking and return nothing (it happened once). Repeat runs before drawing firm conclusions.
- `results/*/raw/` (the models' full answers) is not committed; `summary.json`, `rescored.json` and `REPORT.md` are.
