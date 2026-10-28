# Dictate

A Wispr Flow replacement for Windows (and later Android): hold the Copilot key to talk,
double tap it for hands-free, and the text is typed into whatever app has focus. Transcription
runs locally or through xAI's Grok speech-to-text.

Design doc: https://claude.ai/artifact/XkkRPGspyZsoPMQ8oK2HX8

## What's here so far

| Path | What it is |
| --- | --- |
| `src/Dictate.Core` | Platform-free logic. `KeyStateMachine` turns Copilot key events into push-to-talk, double-tap hands-free and cancel. |
| `tests/Dictate.Core.Tests` | Unit tests for the state machine (run anywhere with `dotnet test`). |
| `src/Dictate.KeySpike` | A small packaged WinUI 3 app that registers as a **Copilot key provider** and logs every tap, hold and release Windows sends it, plus the state machine's decisions. |

## Try the key test

### Option A: download from CI (no Visual Studio needed)

1. Open the latest **build** run under the repo's Actions tab and download `dictate-key-spike-x64`
   (or `-ARM64` on a Snapdragon laptop). Unzip it.
2. In an **elevated** PowerShell, in that folder: `powershell -ExecutionPolicy Bypass -File .\Install.ps1`
3. Settings > Personalization > Text input > **Customize Copilot key** > choose **Dictate**.
4. Open **Dictate (key test)** from Start. The banner turns green when Dictate owns the key.

### Option B: build on your PC

Needs Visual Studio 2022 with the *WinUI application development* workload and the .NET 9 SDK.
Run `scripts\Build-Local.ps1` from an elevated PowerShell. It creates a local signing
certificate (`CN=Dictate Dev`) the first time, builds the MSIX, and installs it.

### What to check

- Hold the key: you should see `HoldStart` after about 300 ms, then `HoldEnd` on release, and the state goes `PushToTalk` then `Idle`.
- Double tap quickly: `Tap`, `Tap`, state `HandsFree`. Tap again to stop.
- One tap: state returns to `Idle` after 400 ms.
- The source column says `fast path` while the app is running and `uri` on a cold start. Please paste the log (Copy log button) back into the project thread.

## Why packaging and signing

Windows only offers packaged, signed apps in the Copilot key picker
([provider docs](https://learn.microsoft.com/windows/apps/develop/windows-integration/microsoft-copilot-key-provider),
[key state docs](https://learn.microsoft.com/windows/apps/develop/windows-integration/copilot-key-state)).
The CI build signs each run with a throwaway self-signed certificate, which `Install.ps1` trusts
on your machine.
