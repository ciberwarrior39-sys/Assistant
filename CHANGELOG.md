# What's new

The newest version is first. The app shows this in Settings > What's new, and each GitHub release uses its section.
Each version starts with a line "## <version> (<month> <year>)", and each change is one line starting with "- ".
The first three lines of a version are the highlights shown after an update.

## 0.9.0 (in progress)
- **Follow-ups:** it keeps track of the last few exchanges, so "and tomorrow?" after a calendar question, "did he reply?" or "and on my work email?" are understood. When a message isn't recognised, the local model rewrites it into a complete request and the app tries again; this happens on your computer, and cloud chat still only ever gets your own words.
- **Found files:** after a file search, "open it", "open the second one", "open the pdf" or "show it in the folder" (and "отвори го", "второто") work on the files just shown, right away and on your computer. With several files, "open it" asks which one with a button for each. Programs and scripts still only run after you press "Run it".
- **Folders:** "find the daskalo folder" (or "намери папката daskalo") looks for folders, and "anything called daskalo?" shows folders with that name next to the files. Right after a search, "a folder, perhaps?" or "а папка?" looks again for folders. Folders open in Explorer.
- **Questions about what was found:** "which one is just a folder?", "what's in them?" or "кое е просто папка?" after a search says what each one is: a plain folder (with a couple of its files), a Visual Studio, Unity, Node or Python project, a Git repository, or for a file its type and size. Read from your disk, on your computer. Then "open the folder", "the one that's just a folder", "the project one" or "the desktop one" picks by what or where it is, and buttons for items with the same name say where each one is. Those buttons take the colour of the file cards: yellow for folders, blue for Word documents, green for spreadsheets and so on.

## 0.8.1 (November 2026)
- **"Hey Assistant":** the wake word now has its own phrase instead of "Hey Jarvis". It is still off until you turn it on in Settings > Voice; if it misses you or wakes by itself, move the sensitivity slider there.
- **Several mailboxes:** add as many as you like in Settings > Calendar and mail, each with a label of your own ("work", "personal"). "Any new emails?" looks in all of them; "any new work emails?" and "did Ivan reply on my work email?" in one. The mailbox from 0.8.0 is kept, labelled "personal".

## 0.8.0 (November 2026)
- **Show me where to click:** ask "where is the save button?" or "where do I click to change the font?" and a ring appears around the right control in the window you were using. It only points: it never clicks for you. Still can't see it? One button and the character walks over and stands next to it.
- **Chat that acts:** say what you want in your own words ("it's too quiet in here") and cloud chat can offer the matching action as a button: open an app, change the volume, check the PC, start a backup. Nothing runs until you press it.
- **Wake word (off until you turn it on):** say the wake phrase and then your question, no keys needed. It is heard on this computer only, and the microphone closes during games, calls and while the PC is locked. Settings > Voice also has a new choice for speaking Bulgarian.
- **Calendar and mail:** "what's on my calendar today?", "when is my next meeting?", "any new emails?", "did Ivan reply?". Connect them in Settings > Calendar and mail with your calendar's private link and a mail app password. Read-only, answered on your computer, never sent to cloud chat.
- **Routines:** "every morning at 8 tell me what's on my calendar today" and it asks itself at that time and shows you the answer. If the PC was off, it runs when you're back the same day.
- **New things to ask for:** "shut down my computer" and "restart my pc" (both ask first and can be stopped), "close chrome", "what's my battery level?", "clear my to-do list".
- **Safer saving:** settings, notes, to-dos and reminders can no longer be left empty by a crash or power cut mid-save, and settings keep a spare copy that comes back by itself.
- **Quieter logs:** the app's logs no longer contain file paths, file names or the names of apps you closed.
- **Scam check:** catches look-alike addresses such as "arnazon" and no longer flags real sites that merely contain a brand's name.
- **Fewer crashes:** a file that is busy, a failed background task or a network hiccup during an action now gives a plain note instead of an error box.

## 0.7.0 (November 2026)
- **PC check-up:** ask "why is my PC slow?" and the assistant shows what's using your memory and processor and which apps start with Windows, with buttons to close a heavy app or stop one starting with Windows (and turn it back on).
- **Backups:** your folders copied to a USB drive or any folder once a day by themselves, as normal files, with older versions of changed or deleted files kept 30 days. Ask "back up my files" to start.
- **Is this a scam?** Mark or paste a message, email or link and ask. The app lists the warning signs it finds (fake delivery fees, look-alike links, requests for codes) and gives a verdict, always with the same safe advice.
- **Internet trouble:** ask "my internet isn't working" and the assistant checks Wi-Fi, the router, the internet, website names and login pages one by one, says where it breaks and what fixes it. "Speed test" measures download, upload and ping.
- **Free up space:** ask "my disk is full" to see temporary files, old installers, the Recycle Bin and your biggest files, and clear them with one button. Installers go to the Recycle Bin first.
- **Your saved facts come first:** a question about something you saved ("who is Kris?", "when is the exam?") is answered from your own facts, on your computer. A Look it up button under it asks the web instead.
- **Fewer pointless lookups:** Wikipedia is only asked about the world. Small talk, follow-ups ("is this…?"), advice, and questions about you or the assistant are answered without it.
- **Articles that don't fit are left out:** when Wikipedia's nearest match isn't about your question, it isn't used or listed as a source.
- **"Look inside your memory"** and similar requests now show what's saved, in English and Bulgarian.
- **Takes less space:** the local model's helper no longer keeps graphics-card files for other kinds of PCs (usually 0.7–1.2 GB freed after the first answer), voices you don't use are removed, and the app's own logs are kept 30 days.
- **Switching to Lite** offers to remove the local model and free about 4.6 GB. Run setup again downloads it back.

## 0.6.0 (November 2026)
- **More accurate answers:** factual questions are looked up on Wikipedia and answered from the article, which is listed under the answer. If it isn't there, the assistant says so instead of guessing, and Search the web looks further.
- **Better at maths and code:** questions that need working out get more thinking time and room for a full answer, and sums are worked out exactly. Code is shown in its own box with a Copy button.
- **Your own shortcuts:** make any key combination run anything you'd type, like Alt+S for "open spotify". Add them in Settings > Shortcuts, or just say "make Alt+S open Spotify".
- **Marked text in Chrome:** Help with selected text now reads what you marked on web pages instead of the address bar, and only uses copied text if you copied it just now.
- **"Translate this":** typing "translate this", "summarise this" or "преведи това" in the chat works on the text you marked.
- **Update choices:** install updates automatically, be told first, or turn them off (Settings > Startup and updates). After an update you'll see what's new, like this.

## 0.5.0 (October 2026)
- **A new look:** the chat is a speech balloon pointing at the character, in the character's own color, with a new typeface and matching menus, Settings and setup.
- **Easier chat:** Enter sends, Shift+Enter starts a new line, ↑ brings back your last message, and a new chat suggests things to try.
- **Any size:** the character's size is now a percentage from 40% to 300%.
- **Settings as pages:** Character, Chat bubble, Voice, Features, Performance, Privacy and cloud, and Startup and updates.
- **A calmer start:** no false "Ollama isn't responding" warning while the local model is still starting.

## 0.4.1 (October 2026)
- **Smarter chat:** plain chat goes straight to a much stronger cloud model, without waiting for the local one.
- **It remembers past chats:** a line or two about each chat is kept for 7 days, so you can ask "what did we talk about yesterday?".
- **Rate answers:** 👍 and 👎 under replies, kept on your computer so bad answers can be looked at.
- **Optional:** let cloud chat know your name and the about-me facts that matter for a message (off unless you turn it on).

## 0.4.0 (October 2026)
- **Help with selected text:** mark text anywhere, right-click the character, then explain, summarise, translate or fix it.
- **Notes, to-do list and reminders**, in English and Bulgarian, kept on your computer.
- **Doing things on the PC:** open apps, folders and sites, volume, music, Windows settings, dark mode, screenshots and more.
- **The character comes alive:** petting, idle animations, reactions to downloads and the battery, break reminders and seasonal hats.
