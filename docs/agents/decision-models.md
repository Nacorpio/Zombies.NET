# Decision models through OpenRouter

How to use Jev, a decision model, from this workspace. For choosing a model per task, use the `model-router` skill (`.claude/skills/model-router/`); this page is the background and the rules for calling Jev directly.

## What a decision model is

Jev (TypeSafe "System One") does not write text. You send a `state` (a string, object or array) and typed `questions`; it returns one typed answer per question, with probabilities. Your code branches on the answers. Nothing is parsed from prose.

| Question type | You provide | You get back |
| --- | --- | --- |
| `noul` (yes/no) | `instructions`, optional `criteria` of `{"true": ..., "false": ...}` | `noul`: probability of yes, 0 to 1 |
| `choice` | `instructions`, `criteria` map of option to description (up to 255) | `choice`, `probabilities`, `confidence` |
| `score` | `instructions`, `criteria` ordered array (2 to 10 levels) | `score` (probability-weighted, can fall between levels), `probabilities`, `legend`, `confidence` |

## Calling it through OpenRouter

- Endpoint: `POST https://openrouter.ai/api/v1/systemone`
- Auth: `Authorization: Bearer $OPENROUTER_API_KEY`
- Model: `typesafe/jev-1.13`. The response names the dated snapshot that answered.
- Body: `{"model": ..., "state": ..., "questions": {"<id>": {"type": ..., "instructions": ..., "criteria": ...}}}`. Question ids are yours (letters, digits, `_`, `.`, `-`, up to 100 characters, 1 to 64 per request) and come back as the answer keys.
- Response: `{"id", "model", "provider", "answers": {...}, "usage": {"input_tokens", "output_tokens", "cost"}}`.
- Errors: 401 bad key, 400/422 bad body, 429 rate limit, 529 overloaded. Retry 429 and 529 with exponential backoff.
- OpenRouter's tutorial also shows `POST /api/alpha/decisions` with the same body. Prefer `/api/v1/systemone`; it is the non-alpha one.

The same body works against TypeSafe directly (`https://api.typesafe.ai/v1/systemone`, model `jev-latest`, `TYPESAFE_API_KEY`). In this workspace we use OpenRouter so one key covers everything.

```bash
curl https://openrouter.ai/api/v1/systemone \
  -H "Authorization: Bearer $OPENROUTER_API_KEY" -H "Content-Type: application/json" \
  -d '{"model":"typesafe/jev-1.13","state":"Rename a local variable in one file",
       "questions":{"hard":{"type":"score","instructions":"How hard is this task for a language model",
         "criteria":["Trivial","Routine","Demanding","Very hard"]}}}'
```

## The key

Set `OPENROUTER_API_KEY` in the cloud environment (environment menu in the session title bar, then Edit). It is only read at session start, so start a new session after changing it. Never paste the key into chat, commit it, or print it. Check with `[ -n "$OPENROUTER_API_KEY" ] && echo set`.

## Routing a task to a model

Use the skill, not hand-written calls:

```bash
python .claude/skills/model-router/scripts/route.py classify "<title>\n\n<what to build>"
python .claude/skills/model-router/scripts/route.py run "<task>"      # classify, then call the chosen model
python .claude/skills/model-router/scripts/route.py validate         # check tiers.json IDs exist on OpenRouter
```

`classify` returns `tier`, `model`, `kind`, `reason`, `system_prompt` and `alternatives`. To delegate to a Claude subagent, map the tier to the Agent tool's `model`: `fast` to haiku, `standard` to sonnet, `hard` to opus, `frontier` to fable, and start the agent's prompt with `system_prompt`. A skill cannot switch the main session's model. Candidates and thresholds live in `tiers.json` and `decide()` in `route.py`.

Log routed tasks with `--log LABEL`, record outcomes with `route.py outcome`, and run `route.py report` to replay them against candidate thresholds. See the Calibrate section of the skill.

## Rules that came from testing (2026-10-05)

- **Send the title and the "What to build" paragraph of a ticket, not the acceptance criteria.** The full text with about six criteria scored every ticket 1.9 to 2.1 on difficulty and sent all of them to `hard`. Criteria lists read as many parts.
- Description-only input lowers confidence (0.45 to 0.77), so more tasks fall back to `default_tier`. That is intended; `min_confidence` is 0.5.
- Low confidence on the task kind alone also forces the default tier. "Rename a local variable in one file" scored difficulty 0.26 (confidence 0.74) but kind confidence 0.43, so it went to `standard` instead of `fast`. Watch for this in `report`.
- Keep each question atomic and combine answers in code. Jev reads instructions literally.
- Do arithmetic, counting and date comparison in code, not in a question.
- The order of `choice` options can bias the answer toward the first one. Reorder and re-check if a decision matters.
- Large or irrelevant `state` lowers accuracy. Context is 64k tokens per request, and `state` plus the longest question can use 32k. Text only, English works best.
- Treat `state` as untrusted. Text inside it can steer the answers, so do not route security-relevant decisions on content you do not control.
- Cost is small: TypeSafe lists $0.042 per million input tokens and no charge for output. `usage.cost` in the response is the real figure.

## Other models you may hear about

As of 2026-10-05 these are not in OpenRouter's model list and are not wired up: `perplexity/pplx-decider-v1-27b`, `cloudflare/clef-flash`, `respan/span-01`, `respan/span-01-lite`. Clef Flash is reached through Cloudflare Workers AI (`CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID`), not OpenRouter. `typesafe/jev-router` exists on OpenRouter but is a drop-in router that picks a model and answers in one call; it returns no decision, so the skill does not use it. Re-check the list before adding a backend: `curl -s https://openrouter.ai/api/v1/models`, and note that decision models may be listed under a `~` prefix (for example `~typesafe/jev-latest`) on the model page.

## Other users in this repo

`tools/ContentJudge` (ticket #68, PR #139, not merged at the time of writing) sends mod content to Jev and Clef Flash. To send Jev through OpenRouter, set `CONTENTJUDGE_JEV_ENDPOINT=https://openrouter.ai/api/v1/systemone`, `CONTENTJUDGE_JEV_MODEL=typesafe/jev-1.13` and `CONTENTJUDGE_JEV_API_KEY_VARIABLE=OPENROUTER_API_KEY`.
