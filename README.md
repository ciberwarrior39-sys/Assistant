# Assistant

A modern, smarter Clippy-style desktop assistant for Windows. Free, local-first, and transparent:
everything it learns lives in plain files in `Documents\Assistant\`.

## Status: build steps 1–5

A placeholder character in the corner of the screen. Click it to chat in a speech bubble: the local model
finds files on your computer, and optional free cloud chat handles conversation.

- **Click** the character: open or close the chat bubble (Esc or clicking elsewhere also closes it).
- **Drag** it anywhere; it snaps to screen edges and remembers its spot (`placement.json` in the data folder).
- **Right-click** it, or use the **tray icon**: open data folder, hide for 1 hour, quit.
- Its color and label show what it's doing: thinking, found it, hmm? (nothing found), tired (cloud unavailable), zzz (idle 10 minutes).
- **Activity log:** which app and window title is in front, and for how long, in `activity\<date>.jsonl`. Idle time,
  the assistant itself and anything in `exclusions.json` are not recorded. Ask "what was I working on today?".
  Turn it off with `"activityLogging": false` in `config.json`.
- **Game mode:** a fullscreen game (or fullscreen video, or a presentation) hides the character, unloads the local
  model from the GPU and sends chat to the cloud only. Add undetected games to `gameExecutables` in `config.json`.
- **7-day retention:** `activity\`, `feedback\` and `sent\` files older than 7 days are deleted automatically.

- `src/Assistant.Core`: the logic, no WPF. Ollama client, tool-calling loop, `search_files` tool (Everything), data folder, dependency checks, diagnostic log.
- `src/Assistant.App`: thin WPF shell. The overlay character, chat bubble, tray icon and the once-a-second foreground watcher.

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
