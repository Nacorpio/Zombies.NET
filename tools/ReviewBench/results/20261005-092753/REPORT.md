# First run, 2026-10-05

Base: PR #137 (context menu, 5 files). Eight bugs planted in `ContextMenu.cs`; tests omitted from the review input; the ticket and the author's notes included.
Validation: with no bugs, the 45 context-menu tests pass; each bug applied alone fails at least one test (B6 and B7 fail one each, B4 fails seven). So each is a real defect.
Each model reviewed the seeded diff and the unmodified diff once (temperature 0, max 12,000 output tokens).

| Model | Found (of 8) | Findings on unmodified diff | Cost (both runs) | Missed |
|---|---|---|---|---|
| Fable 5.1 | 8 | 1 (low) | $0.404 | |
| GPT-5.6 Sol | 8 | 1 (low) | $0.067 | |
| Sonnet 5.5 | 7 | 0 | $0.071 | B1 |
| Opus 5.5 | 7 | 2 (low/medium) | $0.127 | B1 |
| Gemini 3.8 Flash | 6 | n/a (run hit the output limit, empty answer) | $0.072 | B1, B5 |
| Haiku 4.5 | 3 | 2 (both false alarms) | $0.017 | B1, B3, B5, B6, B8 |

Scoring notes (the rules changed after the results were seen, so both are shown):
- The first scorer required the file name to match. Opus cited a non-existent `ContextMenuHost.cs` with correct line numbers, so file names are now ignored when all bugs are in one file.
- B1's symptom shows at line 190 (where the divider is placed), not at line 91 where the check was removed. Both lines count. Only Fable and Sol flagged it.
- Original strict results: Haiku 3, Sonnet 7, Opus 5, Fable 7, Sol 7, Gemini 6.

Findings on the unmodified diff: three models (Opus, Fable, Sol) noted that the highlight, and so the disabled-reason tooltip, stays when the pointer leaves an entry. The code documents that behaviour, so it is a design remark more than a bug. Haiku's two were wrong. Opus also questioned row height at large UI scales, which was not checked.

An early check of the bugs used a class filter that skipped `ContextMenuHostTests` and wrongly suggested B6 and B7 were untested. The corrected check shows both are caught.

Limits: one PR, eight bugs, one run per model, no repeats. The planted bugs mostly violate stated requirements, which is easier than subtle concurrency or data bugs.
