# Dictate

A Wispr Flow replacement for Windows (and later Android): hold the Copilot key to talk,
double tap it for hands-free, and the text is typed into whatever app has focus. Transcription
runs locally or through xAI's Grok speech-to-text.

Design doc: https://claude.ai/artifact/XkkRPGspyZsoPMQ8oK2HX8

## What's here so far

| Path | What it is |
| --- | --- |
| `src/Dictate.Core` | Platform-free logic: the Copilot key state machine, the Grok streaming transcriber (`wss://api.x.ai/v1/stt`), the pre-roll buffer and the dictation session controller. |
| `tests/Dictate.Core.Tests` | Unit tests for all of the above (run anywhere with `dotnet test`). |
| `src/Dictate` | The packaged WinUI 3 app: Copilot key provider, microphone capture, typing into the focused app, the recording pill and the settings window. |
| `src/FlowKey` | **Copilot Key Launcher** (package `FlowKey`), a small helper that makes the Copilot key open any app you choose (see below). |

## How it works

1. Windows sends the Copilot key's tap, hold and release to Dictate (a window message while it runs, a `dictate-key://` launch otherwise).
2. The state machine turns those into push-to-talk (hold), hands-free (double tap, tap again to stop) and cancel (Esc).
3. While you talk, 16 kHz mono audio goes to the speech engine and the partial text shows in a small Acrylic pill at the bottom of the screen. The pill never takes focus.
4. When you finish, the final text is typed into the window that had focus when you started, followed by a space.

Windows only reports a hold after 300 ms. With **Catch the first words** on (the default), the
microphone stays open in the background and the last half second of audio is prepended to each
dictation, so the first word isn't clipped. The audio stays in memory and is dropped unless you
dictate; Windows shows the microphone-in-use icon while it's on.

### Speech engines

- **On this PC (default):** NVIDIA Parakeet TDT 0.6B v3 through [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx),
  on the CPU. Settings downloads the model once (490 MB, into the app's local folder). Audio never
  leaves the PC; the pill previews the text every 1.5 s and the final pass runs when you let go.
- **Grok (xAI cloud):** streams audio to xAI while you talk. Needs an xAI API key, which is stored
  in Windows Credential Manager, never in a file.

## Install

### Option A: download from CI (no Visual Studio needed)

1. Open the latest **build** run under the repo's Actions tab and download `dictate-x64`
   (or `dictate-ARM64` on a Snapdragon laptop). Unzip it.
2. In an **elevated** PowerShell, in that folder: `powershell -ExecutionPolicy Bypass -File .\Install.ps1`
   (this also removes the earlier key-test app).
3. Open **Dictate** from Start and press **Download** next to Local speech model (or switch to Grok and paste an xAI API key).
4. Settings > Personalization > Text input > **Customize Copilot key** > choose **Dictate**.

### Option B: build on your PC

Needs Visual Studio 2022 with the *WinUI application development* workload and the .NET 9 SDK.
Run `scripts\Build-Local.ps1` from an elevated PowerShell. It creates a local signing
certificate (`CN=Dictate Dev`) the first time, builds the MSIX, and installs it.

### Using it

- Hold the Copilot key, talk, let go. The text appears where your cursor was.
- Double tap for hands-free, then tap once more to finish.
- Esc cancels while listening.
- Closing the window keeps Dictate running for the key. **Quit** in settings stops it, and
  **Start with Windows** keeps it ready after you sign in.
- Text can't be typed into apps running as administrator (a Windows rule).
- If something goes wrong, **Diagnostics > Copy log** in settings has the timings and errors.

## Copilot Key Launcher: the Copilot key for any app

Windows only lets packaged, signed apps take the Copilot key, so most desktop apps (Grok Bot,
Electron apps, anything installed from an .exe) can't be chosen in Settings. Copilot Key Launcher
is a Copilot key provider that does one thing: open the app you pick.

- Press the Copilot key: the chosen app opens, or comes back to the front if it's already open.
- Nothing shows up in between, and it's quick. A small resident instance with no XAML and no
  visible window runs from sign-in. Its keyboard hook acts the moment the key goes down (the key
  arrives as Win + Shift + F23; the hook swallows F23 and lets go of Win and Shift without opening
  Start), rather than waiting for Windows to tell a tap from a hold on release.
- If the app already has a window open, the launcher puts it in front directly instead of
  starting the app again (an Electron app's hand-over to its running instance is slow). Otherwise
  it opens it through `shell:AppsFolder`, the way Start does.
- If the resident instance isn't running, Windows launches a `flowkey://` link instead: the app
  still opens without any window in between, just a little later, and that instance stays on for
  the next press.
- Open **Copilot Key Launcher** from Start to choose the app from the All apps list (it defaults to
  Grok Bot if that's installed) and to see the log of presses and timings.

The package is still named `FlowKey` (it began as a helper that drove Wispr Flow's shortcuts), so a
Copilot key setting made for Flow Key keeps pointing at it.

Install: download `flowkey-ARM64` (or `flowkey-x64`) from the latest **build** run, unzip, and run
`Install-FlowKey.ps1` in an elevated PowerShell. It removes Dictate so only one app claims the
key. Then choose **Copilot Key Launcher** under Settings > Personalization > Text input >
Customize Copilot key.

## Why packaging and signing

Windows only offers packaged, signed apps in the Copilot key picker
([provider docs](https://learn.microsoft.com/windows/apps/develop/windows-integration/microsoft-copilot-key-provider),
[key state docs](https://learn.microsoft.com/windows/apps/develop/windows-integration/copilot-key-state)).
The CI build signs each run with a throwaway self-signed certificate, which `Install.ps1` trusts
on your machine.
