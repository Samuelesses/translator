# Game Audio Translator

A Windows desktop app that listens to your game's audio (or any playback
device) and shows a live English translation of what's being said, as an
on-screen overlay — handy for games like FiveM where other players speak a
language you don't.

## How it works

1. **Capture** — Uses WASAPI loopback (via [NAudio](https://github.com/naudio/NAudio))
   to record whatever is playing through a Windows playback device (your
   speakers/headphones, or a virtual audio device you route the game to).
2. **Segment** — A simple energy-based voice activity detector chunks the
   audio into individual utterances (it waits for a pause in speech, or a
   15s cap, before cutting a segment).
3. **Translate** — Each segment is sent to OpenAI's `audio/translations`
   endpoint (`whisper-1`), which transcribes speech in *any* language
   directly into English text in one step.
4. **Display** — The English text appears as a subtitle-style overlay on
   top of your game (transparent, click-through, always-on-top), and in a
   log in the app's main window.

You can choose **what** to capture: an entire playback device (everything
audible through it), or **one specific running application** — e.g. just
FiveM, ignoring Discord/Spotify/etc. entirely — via Windows' Process
Loopback API (Windows 10 2004+ / Windows 11 only).

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or just
  the Desktop Runtime if you only want to run a published build)
- An [OpenAI API key](https://platform.openai.com/api-keys) with access to
  the Whisper API (usage is billed per minute of audio — check OpenAI's
  current pricing before heavy use)

## Building & running

```powershell
git clone <this repo>
cd translator
dotnet restore
dotnet run --project src/GameAudioTranslator
```

Or open `GameAudioTranslator.sln` in Visual Studio 2022+ and hit F5.

To produce a single portable .exe:

```powershell
dotnet publish src/GameAudioTranslator -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The output lands in
`src/GameAudioTranslator/bin/Release/net8.0-windows/win-x64/publish/`.

## Usage

1. Launch the app, paste your OpenAI API key, and click **Save** (it's
   encrypted at rest with Windows DPAPI, tied to your Windows user account).
2. Under **Capture from**, choose one:
   - **This device's audio** — pick a playback device (leave it on
     "Default playback device" unless you've set up a dedicated one), or
   - **A specific application** — launch FiveM first, then pick it from
     the **Application** dropdown (click Refresh if it's not listed yet).
     This captures only FiveM's audio, nothing else on your PC.
3. Click **Start Listening**. Play FiveM normally — translated lines will
   appear both in the app's log and as an overlay over your game.
4. Overlay hotkeys (work even while the game has focus):
   - **Ctrl+Alt+O** — show/hide the overlay
   - **Ctrl+Alt+L** — unlock the overlay so you can drag it somewhere else
     on screen; press it again to lock it back into click-through mode

### Capturing a specific application

"A specific application" uses Windows' Process Loopback API to isolate
just that process's audio (and any child processes it spawns — so picking
either FiveM's launcher or the game process itself works). It requires
**Windows 10 build 19041 (the "May 2020 Update", 2004) or later, or
Windows 11**; on older Windows it'll fail with an error in the status
line — switch to "This device's audio" instead.

The application picker only lists processes with a visible window (so
background services don't clutter it) — make sure FiveM is running and
its window is open before you click Refresh.

If you'd rather isolate FiveM without this mode (e.g. on an older Windows
build), install a virtual audio device such as
[VB-Audio Virtual Cable](https://vb-audio.com/Cable/) (free), set FiveM's
audio output to that virtual device in Windows' volume mixer or FiveM's
own audio settings, and select that virtual device under "This device's
audio". You'll still want your normal speakers as your main default
device for everything else.

### Tuning detection

The **Sensitivity** slider sets the volume (RMS) threshold above which
audio is treated as speech vs. silence. If lines are getting cut short or
missed, lower it; if background noise/music is triggering false segments,
raise it.

## Privacy & cost notes

- Audio segments are sent to OpenAI's API over HTTPS whenever speech is
  detected while listening is active. Nothing is recorded/sent while
  stopped.
- Your API key is stored locally (DPAPI-encrypted, current-user only) in
  `%AppData%\GameAudioTranslator\settings.json` — never committed anywhere
  or sent anywhere except as the `Authorization` header of the OpenAI
  request.
- Whisper API usage is billed by OpenAI per minute of audio processed.

## Project layout

```
src/GameAudioTranslator/
  App.xaml(.cs)              application entry point
  MainWindow.xaml(.cs)       control panel: API key, device picker, log
  OverlayWindow.xaml(.cs)    transparent click-through caption overlay
  Models/                    AppSettings, CaptionLine, AudioDeviceOption, ProcessAudioSource
  Services/
    AudioCaptureService.cs   whole-device WASAPI loopback capture
    ProcessLoopbackCaptureService.cs  per-application loopback capture (raw WASAPI/COM)
    ProcessAudioSourceProvider.cs     lists running apps to capture from
    SpeechSegmenter.cs       shared energy-based VAD segmentation
    AudioConverter.cs        resample captured audio to 16kHz mono WAV
    SpeechTranslationService.cs  OpenAI audio/translations client
    SegmentProcessor.cs      ordered pipeline: convert -> translate -> display
    SettingsStore.cs         load/save settings, DPAPI-encrypt the API key
    HotkeyManager.cs         global hotkeys (RegisterHotKey)
    WindowInterop.cs         click-through window style toggling
    Interop/CoreAudioInterop.cs  raw P/Invoke for the Process Loopback API
    Interop/ActivateAudioInterfaceCompletionHandler.cs  async activation callback
```

## Troubleshooting

- **No captions appear**: confirm the correct output device is selected
  and that it's actually the one playing game audio; try lowering the
  sensitivity slider; check the status line at the bottom of the main
  window for errors.
- **"Couldn't start audio capture"**: another app may have exclusive
  control of the device, or it was disconnected — try Refresh and
  re-select a device.
- **API errors in the status line**: usually an invalid/expired key,
  no remaining quota, or a network issue — the message includes OpenAI's
  error detail.
