# Tool-call ID compatibility probe

Run: `20261028T103210489Z-64c7ea37`

This is a synthetic ID-format probe. It does not establish complete native-history or reasoning replay compatibility, nor cache hit benefits. No ID mappings are applied.

Logical invocations: 20/24. HTTP POST attempts: 16; other HTTP requests (e.g. capability queries): 0. Failures count; no runner retries. Codex's existing credential-reload flow may send a second POST.

## Generated samples

| Source | ID (JSON escaped) | Wire verified | Outcome |
|---|---|---|---|
| haiku55-messages | `"toolu_01AsdH1WtffQYamn5GGErkN5"` | True | completed |
| luna-responses | `"call_45ec05098a0f487f976414fa4fa6885b"` | True | completed |
| luna-codex | `"call_fcc5d114b82842eaa10fa029c2879510"` | True | completed |
| gemini38-flash | `"call_79633"` | True | completed |

## Directed matrix (row source → column target)

| Source | haiku55-messages | luna-responses | luna-codex | gemini38-flash |
|---|---|---|---|---|
| haiku55-messages | PASS | PASS | PASS | local-rejected |
| luna-responses | PASS | PASS | PASS | local-rejected |
| luna-codex | PASS | PASS | PASS | local-rejected |
| gemini38-flash | PASS | PASS | PASS | local-rejected |

PASS requires Completed, no new tool calls, exact random token recovered only from the tool result, real source ID proven on wire, and both outbound call/result IDs unchanged. Other outcomes are not evidence of ID incompatibility.

Gemini native targets currently require GeminiReplayBlock and reject this deliberately synthetic history locally. Such cells are untested format compatibility, not endpoint rejections. HTTP/provider/transport failures need controlled follow-up; this runner does not classify natural-language errors.

Detailed statuses, wire ID fields, HTTP statuses, structured failure facts and usage: [attempts.jsonl](attempts.jsonl). Headers, keys, URLs, raw prompts, response text and reasoning are not written.

