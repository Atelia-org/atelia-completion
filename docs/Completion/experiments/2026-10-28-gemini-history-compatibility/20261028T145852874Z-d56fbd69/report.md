# Tool-call ID compatibility probe

Run: `20261028T145852874Z-d56fbd69`

This is a synthetic ID-format probe. It does not establish complete native-history or reasoning replay compatibility, nor cache hit benefits. No ID mappings are applied.

Logical invocations: 20/24. HTTP POST attempts: 20; other HTTP requests (e.g. capability queries): 0. Failures count; no runner retries. Codex's existing credential-reload flow may send a second POST.

## Generated samples

| Source | ID (JSON escaped) | Wire verified | Outcome |
|---|---|---|---|
| haiku55-messages | `"toolu_013J2JcS1HzTDGfBVPWRVkyF"` | True | completed |
| luna-responses | `"call_9e8f8fad4ef640d89ce76c5c103a8fc4"` | True | completed |
| luna-codex | `"call_2ba7e235a4c143688ca59e6dd0e5856e"` | True | completed |
| gemini38-flash | `"call_1544055"` | True | completed |

## Directed matrix (row source → column target)

| Source | haiku55-messages | luna-responses | luna-codex | gemini38-flash |
|---|---|---|---|---|
| haiku55-messages | PASS | PASS | PASS | PASS |
| luna-responses | PASS | PASS | PASS | PASS |
| luna-codex | PASS | PASS | PASS | PASS |
| gemini38-flash | PASS | PASS | PASS | PASS |

PASS requires Completed, no new tool calls, exact random token recovered only from the tool result, real source ID proven on wire, and both outbound call/result IDs unchanged. Other outcomes are not evidence of ID incompatibility.

Synthetic Gemini tool history uses the documented import signature marker; original call IDs remain unchanged. Native signatures are tested separately with --gemini-history. HTTP/provider/transport failures need controlled follow-up; this runner does not classify natural-language errors.

Detailed statuses, wire ID fields, HTTP statuses, structured failure facts and usage: [attempts.jsonl](attempts.jsonl). Headers, keys, URLs, raw prompts, response text and reasoning are not written.

