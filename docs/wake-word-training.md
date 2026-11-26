# Training the "Hey Assistant" wake word

Until this is done, the wake word is openWakeWord's ready-made **"Hey Jarvis"**. That model is licensed
CC BY-NC-SA 4.0 (non-commercial, share-alike), so the app downloads it on first use instead of shipping it. Training
our own phrase replaces it.

It is done once, in Google Colab, in a browser. Nothing is installed on your PC. About 2.5 hours on the free tier,
almost all of it waiting.

## Steps

1. Open the notebook: <https://github.com/alfiedennen/openwakeword-colab-2026> and press **Open in Colab**.
   (openWakeWord's official notebook no longer runs on Colab's current setup; this one is a patched copy, MIT licence.)
2. **Runtime > Change runtime type**: **T4 GPU** (the free one) and **Runtime version 2026.07**. The notebook needs
   Python 3.12; Colab's newest runtime has 3.13, where one of its packages (`piper-phonemize-cross`) can't be installed.
   An older runtime version takes 2 to 3 minutes to connect. If Colab says "Too many sessions", close the old one
   under **Manage sessions** first. (Colab Pro's L4 with High RAM takes about half the time.)
3. In the settings cell (cell 10 at the time of writing) set:

   ```python
   TARGET_PHRASE = ['hey assistant']
   MODEL_NAME = 'hey_assistant'
   ```

4. **Runtime > Run all**. The install cell (about 1 minute) must end with `Python: 3.12…`, `cuda: True` and
   "All openwakeword deps … import cleanly". If it says `+cpu` or `cuda: False`, stop and check the GPU is selected.
   On the free T4 the whole run took about 1.5 hours (November 2026). Click into the tab every 20 to 30 minutes, keep
   the PC plugged in and awake, and answer any "are you still there?" box at once: a brief internet drop is fine, but a
   runtime disconnected by Colab starts over.
5. The last cell downloads **`hey_assistant.onnx`** (about 0.8 MB). Above it, the notebook prints how good it is:
   `recall@0.5` (the share of test phrases it caught; higher is better) and `FP/hour at 0.5` (false wakes an hour on
   ordinary audio; lower is better). The first model scored 0.47 and 2.0, short of the notebook's own target, yet it
   worked well with a real voice; the test uses synthetic voices only.
6. Put it at `src\Assistant.App\wake\hey_assistant.onnx` (create the `wake` folder). The build copies it next to the
   app, and from then on the phrase is "Hey Assistant" and "Hey Jarvis" is no longer downloaded.
   To try it without rebuilding, drop the file into `%LocalAppData%\Assistant\models\wake` and restart the app.

## How it is made

The notebook has a text-to-speech model (Piper) say the phrase a few thousand times in different voices, mixes those
with room echo and background noise, and trains a small network to tell them from hours of other speech and music.
The voices are English, which is why the phrase has to be English.

## If it wakes too often, or not often enough

- `WakeWordDetector.Threshold` (0.5) is how sure the model has to be. 0.6 to 0.7 means fewer false wakes; 0.3 to 0.4
  hears a quiet or accented "Hey Assistant" more easily.
- Training again with more examples (the notebook's sample-count settings) helps more than the threshold does.
- Variants can be trained together: `TARGET_PHRASE = ['hey assistant', 'hey, assistant']`.

## Licence, as far as it is known

openWakeWord's code is Apache 2.0, and so is the shared embedding model (it comes from Google's speech embedding
model). The notebook is MIT. The notebook does not state a licence for the model it produces, and its background
audio comes from public datasets with their own terms. Check this before using the trained model commercially.
