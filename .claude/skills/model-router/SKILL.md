---
name: model-router
description: Pick the right model for a task by classifying it with Jev (TypeSafe System One) and routing in code to a model on OpenRouter. Use when delegating work to a subagent or another model and the cheapest adequate model is not obvious, or when the user asks to route or choose a model by task.
---

# Model router

Jev (`typesafe/jev-1.13` on OpenRouter, `POST /api/v1/systemone`) is a classifier, not a router: it answers typed questions about the task with probabilities.
`scripts/route.py` asks three atomic questions (kind, difficulty, high stakes), then applies the
rule in `decide()` to map the answers to a tier and an OpenRouter model from `tiers.json`.
Low confidence (< `min_confidence`) falls back to `default_tier`.

## Use

```bash
export OPENROUTER_API_KEY=...      # used for both Jev and the routed model
python .claude/skills/model-router/scripts/route.py classify "<task text>"
# {"tier":"hard","model":"anthropic/claude-opus-5.5","kind":"code","reason":"..."}
```

- To delegate to a Claude subagent, map the tier to the Agent tool's `model` (`fast`→haiku,
  `standard`→sonnet, `hard`→opus, `frontier`→fable). Claude Code cannot switch the main session's model from a skill.
- `classify` also returns a `system_prompt`: the template for the task kind from `tiers.json` (`system_prompts.kinds`), plus the high-stakes addendum when Jev's high-stakes answer is 0.5 or more. Use it as the start of a subagent's prompt. When Jev is not confident about the kind, the generic `default` prompt is used instead.
- To run a non-Claude or specialist model (e.g. `kind_overrides`), use `run`. It sends `system_prompt` as the system message and the task as the user message, and returns the answer with the decision.
- Send only the task text Jev needs. `state` is capped at 32k tokens, and large or irrelevant text lowers accuracy.
- For an issue or ticket, send the title and the "What to build" paragraph, not the acceptance criteria. Live test on four ready-for-agent issues (2026-10-05): the full text, with its six or so criteria, scored every issue 1.9-2.1 difficulty and sent all four to `hard`; the description alone spread them across `hard`, `standard` and the default fallback. Criteria lists read as many parts, which inflates difficulty.
- Description-only input lowers Jev's confidence (0.45-0.77 on difficulty), so more tasks fall back to `default_tier`. That is the intended safe outcome, but it means `min_confidence` matters more for ticket-style input.

## Calibrate

Thresholds are guesses until they are checked against outcomes. Log every routed task and say afterwards how it went:

```bash
route.py classify --log "#17" "<task>"                          # also stores the raw Jev answers (never the task text)
route.py outcome "#17" fit=right result=ok duration_s=600 tokens=150000 tool_uses=40
route.py report                                                # table plus an offline replay against candidate cutoffs
```

- `fit` is your verdict on the model that was used: `under` (needed a stronger one), `right`, or `over` (a weaker one would have done). `result` is `ok`, `rework` or `failed`.
- `report` replays every entry that has raw answers and a `fit` against shifted `cutoffs` and different `min_confidence` values, so thresholds can be tuned without calling Jev again. It warns while there are fewer than 20 replayable entries; the Jev cookbook calibrates on 100 to 200.
- The log is `calibration/log.jsonl`, committed so other sessions append to it. The six entries seeded from the first tickets have no raw answers (they were not saved) and are not replayable.
- `cutoffs` and `stakes_bump` in `tiers.json` are the values `decide()` uses.

## Maintain

- `tiers.json` has one primary model per tier (Claude, so tiers map onto subagent models) plus an `alternatives` directory: `tier` and `kind` lists of other OpenRouter models, ranked by my judgment from price and context size, **not benchmarked**. `classify` returns the ones that apply as `alternatives`, kind-specific first. To use one, call it yourself on OpenRouter; `run` always uses the primary. Only `anthropic/*` tiers can be a subagent `model`.
- Edit `system_prompts` in `tiers.json` to change the per-kind templates; they are fixed text, not generated per task.
- Edit `tiers.json` to change candidates. Run `route.py validate` after; it checks IDs against OpenRouter's public model list.
- Tune thresholds in `decide()` against real tasks. Routing is only as good as those cutoffs.
- `route.py decide < answers.json` runs the rule offline for testing.

## Not wired up

`perplexity/pplx-decider-v1-27b`, `cloudflare/clef-flash`, `respan/span-01` and `respan/span-01-lite`
are not in OpenRouter's model list (checked 2026-10-05), and the Cloudflare Claude Code page does not mention Clef.
`typesafe/jev-router` routes and answers in one call and returns no decision, so this skill does not use it. Add a backend only after confirming its API.
