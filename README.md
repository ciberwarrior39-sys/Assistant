# Assistant

A modern, smarter Clippy-style desktop assistant for Windows. Free, local-first, and transparent:
everything it learns lives in plain files in `Documents\Assistant\`.

## Status: build steps 1–9, plus the setup wizard from step 10

A placeholder character in the corner of the screen. Click it to chat in a speech bubble: the local model
finds files on your computer, and optional free cloud chat handles conversation.

- **Click** the character: open or close the chat bubble (Esc or clicking elsewhere also closes it).
- **Drag** it anywhere; it snaps to screen edges and remembers its spot (`placement.json` in the data folder).
- **Right-click** it, or use the **tray icon**: settings, open data folder, hide for 1 hour, quit.
- **Settings** (right-click or tray): a color for every mood, size, labels, calm animations, light or dark chat bubble,
  text size, voice and speed with a test button, push-to-talk keys, features on/off and cloud keys. Changes show up
  live on the character; Cancel puts everything back. Saved in `config.json` (the `appearance` part holds the look).
- Its color and label show what it's doing: thinking, found it, hmm? (nothing found), tired (cloud unavailable), zzz (idle 10 minutes), ! (hand raised), ! weekly review (waiting for you), listening…, speaking, time's up!.
- **Activity log:** which app and window title is in front, and for how long, in `activity\<date>.jsonl`. Idle time,
  the assistant itself and anything in `exclusions.json` are not recorded. Ask "what was I working on today?".
  Turn it off with `"activityLogging": false` in `config.json`.
- **Game mode:** a fullscreen game (or fullscreen video, or a presentation) hides the character, unloads the local
  model from the GPU and sends chat to the cloud only. Add undetected games to `gameExecutables` in `config.json`.
- **7-day retention:** `activity\`, `feedback\` and `sent\` files older than 7 days are deleted automatically.
- **Memory:** say "remember that my CV is in D:\Jobs" or "remember I like short answers"; ask "what do you know about me?";
  say "forget my CV folder". Facts live in `memory\about-me.md`, `preferences.md` and `places.md`, one line each
  starting with `- `, and edits apply right away. Only `preferences.md` is sent with cloud chat; the rest never leaves
  this computer, and "remember…"/"forget…" messages are never sent to the cloud.
- **Weekly review:** once a week the character shows "weekly review"; open the bubble to Keep (or edit, then Keep) or
  Forget up to 5 facts noticed in the activity log. Unanswered after 2 days means forgotten. State is in `review.json`.
  The tray menu's **Weekly review now** runs it early (it needs a few days of activity to find anything).
- **Hand-raise:** a silent "!" when you seem stuck: going back and forth between an app and search pages
  (3 times in 5 minutes), or switching windows a lot and then sitting still in one. Open the chat to see why; ignored,
  it lowers after 30 seconds. Every hand-raise is logged in `feedback\<date>.jsonl`; after 3 ignores in a row,
  `searchLoopTrips` or `stuckSwitches` in `config.json` goes up by one. Turn it off with `"handRaiseEnabled": false`.
- **Voice:** hold **Ctrl+Alt+Space** while talking and let go when done. Speech is turned into text on this computer
  (Whisper base.en on the CPU; the ~140 MB model downloads to `%LocalAppData%\Assistant\models` on the first press),
  and the reply is read aloud with a Windows voice. Typed questions stay silent. Audio is never saved. Closing the
  bubble or pressing the keys again stops the voice. Settings: `pushToTalkKey`, `voiceName` (e.g. "Zira"),
  `voiceRate` (-10 to 10), `voiceEnabled`. Off in game mode.
- **Screensaver:** a few minutes before Windows turns the screen off (its own "turn off my screen after" setting),
  the character bounces around the main screen over the desktop, changing color at each edge. Any mouse or keyboard
  input sends it back. Not during games, fullscreen video, or when a video player keeps the screen on.
  Settings > Features: on/off, how many minutes before, and "Try it now".
- **Quick answers:** "flip a coin", "heads or tails", "roll 2 dice", "roll a d20", "pick a number between 1 and 50",
  "pick pizza or sushi", "what time is it", "what's the date", "what's 12 times 7", "15% of 80",
  "set a timer for 5 minutes", "remind me in 10 minutes to check the oven", "how much time is left", "cancel the timer".
  Answered instantly by code, not the model (a model can't be random, doesn't know the time and gets sums wrong),
  and they work in game mode too. Timers are said aloud when due and are lost if the app quits.
- **Looking at the screen:** ask "look at this" or "what does this error mean?" and the local model looks at the window
  you were using (never the whole screen) and answers. The picture stays in memory, is never saved and never goes
  to the cloud. Windows in `exclusions.json` and minimized windows are refused. A green "Looked at:" line shows which window.

- `src/Assistant.Core`: the logic, no WPF. Ollama client, tool-calling loop, tools, file search (Everything, Windows Search), memory, activity, voice, setup (hardware check, embedded Ollama), data folder, diagnostic log.
- `src/Assistant.App`: thin WPF shell. The overlay character, chat bubble, tray icon and the once-a-second foreground watcher.

## First run: the setup wizard

On first start (or tray > **Run setup again**) a wizard sets everything up, so nobody downloads anything by hand:

1. **Welcome:** what stays private, and whether to keep the activity log.
2. **Brain:** checks the graphics card and picks the model (NVIDIA 4–6 GB: `qwen3.5:2b`; 8 GB+: `qwen3.5:4b`;
   no usable GPU: `qwen3.5:0.8b` on the CPU, cloud recommended). Uses an Ollama that is already installed if there is
   one; otherwise downloads the app's own copy to `%LocalAppData%\Assistant\ollama` and runs it on port 11435, with
   models in `%LocalAppData%\Assistant\ollama-models`. Progress bars throughout.
3. **File search and voice:** offers to install Everything (otherwise Windows Search is used) and to download the
   speech model now.
4. **Cloud chat (optional):** Groq recommended, OpenRouter as the alternative, with a Check button for the key.
5. **Done:** a few tips, and the assistant appears.

Cloud keys are saved encrypted for the Windows user (DPAPI) as `"dpapi:..."` in `config.json`.

## Requirements (development)

- .NET 10 SDK
- `lib\Everything64.dll` from the Everything SDK (not committed; see `.gitignore`)
- Ollama, Everything and the models are set up by the wizard

## Run

```
dotnet run --project src\Assistant.App
```

## Optional: smarter chat with a free cloud model

The local model handles every message first. Anything that uses a tool (files) stays on this computer.
Plain conversation goes to a cloud model when a key is set up; every request is logged in `Documents\Assistant\sent\`.

1. **Groq (recommended):** most free volume. Sign up at console.groq.com (needs phone verification), create an API key.
2. **OpenRouter (alternative):** signs up without a phone or card, but only about 50 free requests a day.

Add the key in the setup wizard or in Settings > Features (it is stored encrypted), then restart.
With both set, Groq is tried first and OpenRouter takes over when Groq's free quota runs out.

## Where things are

| What | Where |
| --- | --- |
| User data (memory, logs, config) | `Documents\Assistant\` |
| Settings | `Documents\Assistant\config.json` (restart after editing) |
| Everything sent to a cloud model | `Documents\Assistant\sent\` |
| Developer log (errors, timings, never message text) | `%LocalAppData%\Assistant\logs\` |
