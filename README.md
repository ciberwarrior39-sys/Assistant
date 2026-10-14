# Assistant

A modern, smarter Clippy-style desktop assistant for Windows. Free, local-first, and transparent:
everything it learns lives in plain files in `Documents\Assistant\`.

## Status: build steps 1, 2 and 5

A chat window where the local model finds files on your computer, with optional free cloud chat.

- `src/Assistant.Core`: the logic, no WPF. Ollama client, tool-calling loop, `search_files` tool (Everything), data folder, dependency checks, diagnostic log.
- `src/Assistant.App`: thin WPF shell. A plain chat window for now; the overlay character comes in step 3.

## Requirements (development)

- .NET 10 SDK
- Ollama running, with the model pulled: `ollama pull qwen3.5:2b`
- Everything (voidtools, 1.4) running
- `lib\Everything64.dll` from the Everything SDK (not committed; see `.gitignore`)

## Run

```
dotnet run --project src\Assistant.App
```

## Optional: smarter chat with a free cloud model

The local model handles every message first. Anything that uses a tool (files) stays on this computer.
Plain conversation goes to a cloud model when a key is set up; every request is logged in `Documents\Assistant\sent\`.

1. **Groq (recommended):** most free volume. Sign up at console.groq.com (needs phone verification), create an API key.
2. **OpenRouter (alternative):** signs up without a phone or card, but only about 50 free requests a day.

Paste the key into `Documents\Assistant\config.json` (`groqApiKey` or `openRouterApiKey`) and restart.
With both set, Groq is tried first and OpenRouter takes over when Groq's free quota runs out.

## Where things are

| What | Where |
| --- | --- |
| User data (memory, logs, config) | `Documents\Assistant\` |
| Settings | `Documents\Assistant\config.json` (restart after editing) |
| Everything sent to a cloud model | `Documents\Assistant\sent\` |
| Developer log (errors, timings, never message text) | `%LocalAppData%\Assistant\logs\` |
