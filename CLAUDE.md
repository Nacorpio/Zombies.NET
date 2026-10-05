## Agent skills

### Issue tracker

Issues are tracked in GitHub Issues for `Nacorpio/Zombies.NET` (via the `gh` CLI). See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-label vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `GLOSSARY.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.

### Decision models

Jev and other decision models are called through OpenRouter. A `model-router` user skill (installed in `~/.claude/skills`, not in this repo) picks a model per task when present. See `docs/agents/decision-models.md`.
