# Assistant

A modern, smarter Clippy-style desktop assistant for Windows. Free, local-first, and transparent:
everything it learns lives in plain files in `Documents\Assistant\`.

**English** · [Български](#изтегляне)

## Download

### [⬇ Download for Windows](https://github.com/ciberwarrior39-sys/Assistant/releases/latest/download/DesktopAssistant-win-Setup.exe)

Always the newest version (Windows 10/11, 64-bit, about 90 MB). Or see the
[download page](https://ciberwarrior39-sys.github.io/Assistant/) or [all versions](https://github.com/ciberwarrior39-sys/Assistant/releases).
You don't need "Code > Download ZIP": that's the source code, not the app.

1. **Open** `DesktopAssistant-win-Setup.exe`. No administrator rights needed.
2. **If Windows says "Windows protected your PC":** click **More info**, then **Run anyway**. Windows shows this for new
   apps without a paid code-signing certificate; the full source code is here for anyone to check.
3. **Follow the setup wizard.** It checks your PC, downloads the right AI model and asks a few questions. Then the
   character appears in the corner of the screen, with shortcuts on the desktop and in the Start menu.

Updates install themselves. To uninstall: Windows Settings > Apps > Installed apps > Assistant.
[Privacy policy](https://ciberwarrior39-sys.github.io/Assistant/privacy.html) ·
[Code signing policy](https://ciberwarrior39-sys.github.io/Assistant/code-signing.html)

**Needs:** Windows 10 or 11 (64-bit), 8 GB of RAM or more (Lite mode for less), a few GB of free space for the AI model.
A graphics card (NVIDIA or AMD, 4 GB+) is optional and makes the local AI fast. Internet only for setup, updates and
the optional cloud chat.

## Изтегляне

### [⬇ Изтегляне за Windows](https://github.com/ciberwarrior39-sys/Assistant/releases/latest/download/DesktopAssistant-win-Setup.exe)

Винаги най-новата версия (Windows 10/11, 64-битов, около 90 MB). Или вижте
[страницата за изтегляне](https://ciberwarrior39-sys.github.io/Assistant/) или [всички версии](https://github.com/ciberwarrior39-sys/Assistant/releases).
Не ви трябва „Code > Download ZIP“: това е изходният код, не програмата.

1. **Отворете** `DesktopAssistant-win-Setup.exe`. Не са нужни администраторски права.
2. **Ако Windows покаже „Windows protected your PC“** („Windows защити вашия компютър“): натиснете **More info**
   (Повече информация), после **Run anyway** (Изпълни въпреки това). Windows показва това за нови програми без платен
   сертификат за подписване; целият изходен код е тук и всеки може да го провери.
3. **Следвайте съветника за настройка.** Той проверява компютъра, изтегля подходящия модел и задава няколко въпроса.
   После човечето се появява в ъгъла на екрана, с преки пътища на работния плот и в менюто „Старт“.

Обновленията се инсталират сами. Деинсталиране: Настройки на Windows > Приложения > Инсталирани приложения > Assistant.
[Политика за поверителност](https://ciberwarrior39-sys.github.io/Assistant/privacy.html) ·
[Политика за подписване на кода](https://ciberwarrior39-sys.github.io/Assistant/code-signing.html)

**Изисквания:** Windows 10 или 11 (64-битов), 8 GB RAM или повече (режим Lite при по-малко), няколко GB свободно място
за модела. Видеокарта (NVIDIA или AMD, 4 GB+) не е задължителна, но прави локалния ИИ бърз. Интернет е нужен само за
настройката, обновленията и облачния чат по избор. Програмата е на английски, но разбира имена на файлове и много
заявки и на български.

## Features

A placeholder character in the corner of the screen. Click it to chat in a speech bubble: the local model
finds files on your computer, and optional free cloud chat handles conversation.

- **Click** the character: open or close the chat bubble (Esc or clicking elsewhere also closes it).
- **The chat bubble** is a speech balloon with a tail pointing at the character, outlined in the character's Normal
  color. Your messages are tinted notes on the right; replies are plain text at full width (mark any part to copy it).
  **Enter** sends, **Shift+Enter** starts a new line, **↑** in an empty box brings back your last message. A new chat
  starts with suggestions to try. The dot by the name is green when everything works, amber when an optional part
  (cloud chat, Everything) is failing, red when something needed is missing (with a line saying what); hover it for
  details. **⋯** has New chat, Check again, Open data folder and Settings. Found files are rows with their kind, folder
  and date; cards that need your decision (sending to the cloud, the weekly review) have an amber edge.
- **Drag** it anywhere; it snaps to screen edges and remembers its spot (`placement.json` in the data folder).
- **Right-click** it, or use the **tray icon**: settings, open data folder, hide for 1 hour, quit.
- **Characters:** Settings > Character picks the Blob (default), the Robot or the Cat, all drawn by the app in your mood
  colors. **Create my own** makes an empty character in `characters\<name>\` with one folder per mood; fill them in
  Settings (Choose… per mood: an animated GIF, or PNG frames) or drop files in by hand. Only Normal (Idle) is needed;
  other moods use it with a glow in the mood's color plus the usual label. The optional **Cursor nearby** slot (`Noticed`)
  plays instead of Normal while the mouse pointer is close, since custom characters have no drawn eyes to follow it. Pictures are shrunk to 256 px; up to 240 frames
  per mood; PNG frame speed is in the character's `character.json`. Replaced or cleared files go to the Recycle Bin.
- **Sharing characters:** Settings > Character > **Export…** saves one `.assistant-character` file (a zip of the folder,
  with an optional author, description and glow colors). **Import a character file…**, or double-clicking one in the
  installed app, shows a preview first. Only GIF/PNG pictures, `character.json` and `README.txt` are taken from it, into
  a new folder (never over an existing one), with size limits (30 MB a file, 200 MB a character); nothing in it is run.
- **Settings** (right-click, tray or ⋯ in the bubble), as pages on the left: **Character** (look, colors, size from 40% to 300% with a slider or typed exactly, life),
  **Chat bubble** (light or dark, which also applies to menus and Settings; text size), **Voice**, **Features** (web
  search, activity log, game mode, hand-raise, screensaver), **Performance**, **Privacy and cloud** (cloud keys, what
  the cloud may know, deleting your data) and **Startup and updates**. Changes show up live; Cancel puts everything
  back. Saved in `config.json` (the `appearance` part holds the look).
- **One look everywhere:** the bubble, menus (the character's and the tray's), Settings and the setup wizard share the
  same colors and controls, follow the light or dark theme, and use the character's Normal color as their accent.
  The typeface is [Onest](https://github.com/simpals/onest), bundled with the app (Latin and Cyrillic).
- Its color and label show what it's doing: thinking, found it, hmm? (nothing found), tired (cloud unavailable), zzz (idle 10 minutes), ! (hand raised), ! weekly review (waiting for you), listening…, speaking, time's up!.
- **Activity log:** which app and window title is in front, and for how long, in `activity\<date>.jsonl`. Idle time,
  the assistant itself and anything in `exclusions.json` are not recorded. Ask "what was I working on today?".
  Turn it off with `"activityLogging": false` in `config.json`.
- **Game mode:** a fullscreen game (or fullscreen video, or a presentation) hides the character, unloads the local
  model from the GPU and sends chat to the cloud only. Add undetected games to `gameExecutables` in `config.json`.
- **7-day retention:** `activity\`, `feedback\` and `sent\` files older than 7 days are deleted automatically.
- **Memory:** say "remember that my CV is in D:\Jobs" or "remember I like short answers"; ask "what do you know about me?";
  say "forget my CV folder". Facts live in `memory\about-me.md`, `preferences.md` and `places.md`, one line each
  starting with `- `, and edits apply right away. Only `preferences.md` is sent with cloud chat (plus, if you turn on
  **Let cloud chat know about me** in Settings > Features, your name and the `about-me.md` facts that share words with
  the message); `places.md` never leaves this computer, and "remember…"/"forget…" messages are never sent to the cloud.
  The local model gets up to 3 saved facts that share words with each message, so it can use them without being asked.
- **Remembers past chats** (kept 7 days): 2 minutes after the bubble closes, the local model writes a line or two
  about the chat to `conversations\<date>.md` (needs at least two messages; not in game mode or Lite). The local model
  sees the notes that match a new message, and "what did we talk about yesterday?" / `за какво говорихме вчера` lists
  them. Never sent to the cloud; edit or delete the files any time.
- **👍 / 👎** under replies: saves the question, the answer and who answered in `feedback\ratings-<date>.jsonl`
  (kept 7 days), so bad answers can be looked at.
- **Weekly review:** once a week the character shows "weekly review"; open the bubble to Keep (or edit, then Keep) or
  Forget up to 5 facts noticed in the activity log. Unanswered after 2 days means forgotten. State is in `review.json`.
  The tray menu's **Weekly review now** runs it early (it needs a few days of activity to find anything).
- **Hand-raise:** a silent "!" when you seem stuck: going back and forth between an app and search pages
  (3 times in 5 minutes), or switching windows a lot and then sitting still in one. Open the chat to see why; ignored,
  it lowers after 30 seconds. Every hand-raise is logged in `feedback\<date>.jsonl`; after 3 ignores in a row,
  `searchLoopTrips` or `stuckSwitches` in `config.json` goes up by one. Turn it off with `"handRaiseEnabled": false`.
- **Voice:** hold **Ctrl+Alt+Space** while talking and let go when done. Speech is turned into text on this computer
  (Whisper base.en on the CPU; the ~140 MB model downloads to `%LocalAppData%\Assistant\models` on the first press),
  and the reply is read aloud: with a natural Piper voice (US female, US male or UK female; Settings > Voice
  downloads Piper and the voice once, about 86 MB, into `%LocalAppData%\Assistant\piper`), or with a Windows voice. Typed questions stay silent. Audio is never saved. Closing the
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
- **Performance profiles** (setup wizard recommends one from the hardware; Settings > Features changes it after a restart):
  **Full** (local model on the graphics card, stays ready 5 minutes), **Balanced** (small model on the processor that
  leaves memory after 2 minutes, tiny.en speech recognition) and **Lite** (no local model: the router and tools answer
  direct requests, plain chat uses the cloud, screen reading off, 15 fps and still when idle). In every profile the model
  starts loading when the chat bubble opens, and the speech model leaves memory 90 seconds after the last question.
  Privacy is the same in all three. Settings > Features shows what each part uses right now (the assistant, the local
  model and what Ollama has loaded, Piper while speaking, Everything), and the developer log records it every 10 minutes.
- **Instant answers for clear requests:** "find my CV", "where are my tax files", "documents I edited today",
  "what did I do today?", "how long did I spend in Unity?", "what's my name?", "remember that…", "forget…" and
  "look at this" go straight to the right tool, decided by code: no model call, and the reply is built from the result,
  so nothing can be made up. Anything less clear still goes to the model. These also work in game mode
  (except looking at the screen).
- **Doing things on the PC** (English and Bulgarian, answered by code, no confirmation unless noted):
  "open Spotify" / "launch Discord" (any app in the Start menu; a choice of buttons when several match), "open Downloads"
  or a folder saved with "remember…", "open YouTube" / "go to github.com" (an installed app wins over a website, except
  weak matches for well-known sites), "search the web for …" (Google, DuckDuckGo or Bing: Settings > Features),
  "volume up/down", "set volume to 30", "mute", "pause", "next song", "open Bluetooth settings" (Windows Settings pages),
  "Windows settings", "settings" (the assistant's own), "lock my PC", "dark mode" / "light mode", "take a screenshot"
  (the whole screen, saved in Pictures\Screenshots; the character steps aside first; not in game mode), and
  "empty the recycle bin" (asks first, with the item count and size). Bulgarian: `отвори …`, `потърси в интернет …`,
  `намали звука`, `звука на 30`, `пауза`, `следваща песен`, `заключи компютъра`, `тъмен режим`, `направи скрийншот`,
  `изпразни кошчето`. No key presses are simulated: volume goes through Windows' audio interface, media control through
  a Windows command message. "open my CV" isn't an app, folder or website, so file search answers it as before.
- **Notes, to-do list and reminders** (English and Bulgarian, answered by code, private like memory, never sent to the cloud):
  - "note: the wifi password is on the router", "show my notes", "notes about wifi", "delete the note about wifi"
    (`бележка: …`, `покажи бележките`). Kept in `notes.md`, one dated line each.
  - Adding: "add milk to my list", "put homework on my tasks", "new task call the bank", "todo buy milk"
    (`добави хляб в списъка`, `добави задача …`). Checking: "my list", "what do I have to do?", "what's left?",
    "anything to do?" (`какво имам за правене`, `какво ми остава`, `моите задачи`). Ticking off: "cross off milk",
    "milk is done", "I'm done with the homework", "done: milk" (`зачеркни хляб`, `хляб е готово`, `свърших с …`).
    "clear finished tasks" (`изчисти готовите`). The list shows as a
    checklist in the chat; ticking a box saves at once. Kept in `todo.md` as Markdown checkboxes (`- [ ] task`), so any
    notes app can edit it too.
  - "remind me at 18:00 to study", "remind me tomorrow at 9 to call the bank", "remind me on Friday at 10 …",
    "remind me to … in 2 hours", "what are my reminders", "cancel the reminder about the bank"
    (`напомни ми утре в 9 да …`, `напомни ми след 2 часа да …`). The reply says the exact time it set. Timers and reminders
    are kept in `reminders.json`, so they survive a restart; one due while the app was closed is announced at the next
    start ("While I was off, you missed: …"). When one is due: the bubble, the "time's up!" mood, voice, and a Windows
    notification (seen even when the character is hidden).
  - "note …" phrases now go to notes; "remember …" still goes to memory.
- **Help with selected text:** mark text anywhere with the mouse, right-click the character > **Help with selected text**.
  A card shows what was picked up, with **Explain, Summarise, Translate to English / Bulgarian, Fix grammar, Make shorter**
  or your own question; answers have a **Copy** button. The marked text is read through Windows' accessibility interface
  (no simulated key presses, the clipboard is never changed); apps that don't share it (some Chrome pages, Discord) use
  the last copied text instead, and the card says so. The local model answers (up to 2,500 characters, 1,200 for
  translations and rewrites, whose answer is as long as the text); **Ask cloud chat instead…** shows exactly what would
  be sent first. Not in game mode, not from windows in `exclusions.json`, and never added to the chat history.
- **It feels alive** (Settings > Character > Life, every part can be turned off; all of it stops during a game and none
  of it opens the chat):
  - **Petting:** move the mouse back and forth over it and hearts float up; **double-click** it for a little hop ("boop!").
  - **Idle animations:** now and then it blinks, looks around, stretches or hops; late at night it yawns. Not in Lite or
    with calm animations (those keep only the blink).
  - **Reactions:** a finished download ("download done: file.zip", click the label to show it in Explorer; only file
    names in Downloads are seen, nothing is opened), a low battery (20 % or less, once until it's plugged in), plugging
    in, and "welcome back!" after 20 minutes away or a game.
  - **Break reminder:** after an hour at the PC (30 min to 2 h, or off) it stretches and asks "time for a break?".
    A 5-minute pause starts the count over; games don't count.
  - **Seasonal hats** on the Blob, the Robot and the Cat: a pumpkin on 31 October, a Santa hat 20–26 December, a party
    hat on New Year's Eve and Day, and on your birthday, only if you told it to remember it ("remember my birthday is
    14 March"); then it also says happy birthday once that day.
  - **Walking around** the bottom of the screen every few minutes: off by default, since it can be distracting.
- **Copying from the chat:** any part of a message can be marked with the mouse and copied (Ctrl+C or right-click).
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

## Making an installer

**Releases** are built on GitHub: Actions tab > **Release** > Run workflow > type the version (e.g. `0.3.5`) > Run workflow.
It publishes, packs and uploads the release with GitHub's own token (no personal token needed), and keeps the installer
with the run for 14 days. Only these builds are code-signed (see the [code signing policy](https://ciberwarrior39-sys.github.io/Assistant/code-signing.html)).

**Test builds** on your own PC:

```
.\build-release.ps1 -Version 1.0.0
```

This publishes the app self-contained (the other PC needs no .NET) and packs it with Velopack into
`Releases\DesktopAssistant-win-Setup.exe`: one file to share. Installing needs no admin rights and adds Desktop and
Start menu shortcuts; the first start opens the setup wizard.

- **Updates:** add `-Repo https://github.com/<you>/<repo>` and the app checks that repository's GitHub Releases once a
  day, downloads new versions quietly and installs them on the next start (or tray > Restart to update). Add
  `-Token <GitHub token>` to upload the release too. Bump `-Version` every time.
- **Start with Windows:** on by default in the installed app (Settings > Features, or Task Manager > Startup apps).
  The icon is the blob (`src\Assistant.App\assistant.ico`), used by the exe, its shortcuts, the tray and the installer.
- **Uninstall** (Windows Settings > Apps) removes the app, its own Ollama, models and logs (`%LocalAppData%\Assistant`),
  but keeps `Documents\Assistant`. Settings > Features > "Delete all my data" removes that too.
- **SmartScreen:** the installer isn't code-signed, so Windows shows "Windows protected your PC" the first time.
  Click More info > Run anyway.
- **Download link and page:** the README's download link always points at the newest
  `DesktopAssistant-win-Setup.exe` (GitHub's `releases/latest/download/` address), so it never needs changing.
  The download page is `docs\index.html` (English and Bulgarian), served by GitHub Pages: repository
  Settings > Pages > Deploy from a branch > `main`, folder `/docs`. It shows the newest version number by itself.

## Reviews on the download page

The download page (`docs/index.html`) shows the installer's download count (all versions together, from GitHub's
public release list) and a **Reviews and feedback** section. The section stays hidden until it's set up, in the
script near the end of `docs/index.html`:

- **Public reviews** (`repoId`, `categoryId`): GitHub Discussions shown through [giscus](https://giscus.app), loaded
  only when a visitor clicks "Show reviews". Needs Discussions turned on, a `Reviews` category (Announcement format),
  and the giscus app installed on the repository; giscus.app then shows both IDs. Reviews can be hidden or deleted
  in the repository's Discussions tab.
- **Private feedback** (`feedbackForm`): the share link of a free form (for example Tally) for people without a
  GitHub account. Only you see the answers.

## License

[MIT](LICENSE). The bundled Onest font is under the SIL Open Font License 1.1 (`src/Assistant.App/Fonts/OFL.txt`).

## Requirements (development)

- .NET 10 SDK
- `lib\Everything64.dll` from the Everything SDK (not committed; see `.gitignore`)
- Ollama, Everything and the models are set up by the wizard

## Run

```
dotnet run --project src\Assistant.App
```

## Optional: smarter chat with a free cloud model

Plain conversation goes straight to the cloud model when a key is set up, without waiting for the local model: Groq's
`openai/gpt-oss-120b` (when its per-minute quota runs out, `llama-3.3-70b-versatile` answers for 2 minutes). A message
that mentions files, folders, the screen, memory, activity, notes, tasks, reminders or past chats, or that touches a
saved fact, goes to the local model first, and anything that uses a tool stays on this computer. Both models are told
the date and time. Every request is logged in `Documents\Assistant\sent\`.

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
| Notes about past chats (7 days) | `Documents\Assistant\conversations\` |
| Developer log (errors, timings, never message text) | `%LocalAppData%\Assistant\logs\` |
