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
- To run a non-Claude or specialist model (e.g. `kind_overrides`), use `run`. It sends the task as a single user message and returns the answer with the decision.
- Send only the task text Jev needs. `state` is capped at 32k tokens, and large or irrelevant text lowers accuracy.

## Maintain

- Edit `tiers.json` to change candidates. Run `route.py validate` after; it checks IDs against OpenRouter's public model list.
- Tune thresholds in `decide()` against real tasks. Routing is only as good as those cutoffs.
- `route.py decide < answers.json` runs the rule offline for testing.

## Not wired up

`perplexity/pplx-decider-v1-27b`, `cloudflare/clef-flash`, `respan/span-01` and `respan/span-01-lite`
are not in OpenRouter's model list (checked 2026-10-05), and the Cloudflare Claude Code page does not mention Clef.
`typesafe/jev-router` routes and answers in one call and returns no decision, so this skill does not use it. Add a backend only after confirming its API.
