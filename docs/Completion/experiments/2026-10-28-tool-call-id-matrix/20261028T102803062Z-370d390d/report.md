# Tool-call ID compatibility probe

Run: `20261028T102803062Z-370d390d`

This is a synthetic ID-format probe. It does not establish complete native-history or reasoning replay compatibility, nor cache hit benefits. No ID mappings are applied.

Logical invocations: 20/40. HTTP POST attempts: 17; other HTTP requests (e.g. capability queries): 0. Failures count; no runner retries. Codex's existing credential-reload flow may send a second POST.

## Generated samples

| Source | ID (JSON escaped) | Wire verified | Outcome |
|---|---|---|---|
| haiku55-messages | `null` | False | completed |
| luna-responses | `"call_45ec05098a0f487f976414fa4fa6885b"` | True | completed |
| luna-chat | `null` | False | http-failed |
| luna-codex | `"call_59aac89ace9e4a2ab7e89c77ef7aa1b7"` | True | completed |
| gemini38-flash | `"call_1392363"` | True | completed |

## Directed matrix (row source → column target)

| Source | haiku55-messages | luna-responses | luna-chat | luna-codex | gemini38-flash |
|---|---|---|---|---|---|
| haiku55-messages | not-executed | not-executed | not-executed | not-executed | not-executed |
| luna-responses | PASS | PASS | http-failed | PASS | local-rejected |
| luna-chat | not-executed | not-executed | not-executed | not-executed | not-executed |
| luna-codex | PASS | PASS | http-failed | PASS | local-rejected |
| gemini38-flash | PASS | PASS | http-failed | PASS | local-rejected |

PASS requires Completed, no new tool calls, exact random token recovered only from the tool result, real source ID proven on wire, and both outbound call/result IDs unchanged. Other outcomes are not evidence of ID incompatibility.

Gemini native targets currently require GeminiReplayBlock and reject this deliberately synthetic history locally. Such cells are untested format compatibility, not endpoint rejections. HTTP/provider/transport failures need controlled follow-up; this runner does not classify natural-language errors.

Detailed statuses, wire ID fields, HTTP statuses, structured failure facts and usage: [attempts.jsonl](attempts.jsonl). Headers, keys, URLs, raw prompts, response text and reasoning are not written.

