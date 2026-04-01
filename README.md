# Fatima TTS

> Windows desktop client for the [Inworld TTS API](https://docs.inworld.ai/docs/tts/tts.md) — generate unlimited-length audio, manage voices, and batch process files with full resume support.

![Fatima TTS Screenshot](docs/screenshot.png)

## Why?

The official [inworld.ai](https://inworld.ai) website limits each generation to **2,000 characters**, which makes it impractical for long-form content like audiobooks, podcasts, voiceovers, and narrations. Fatima TTS removes that limitation entirely:

| Limitation on inworld.ai | Fatima TTS |
|---|---|
| 2,000 character limit per generation | Unlimited text length — automatically splits and merges |
| No batch processing | Generate multiple jobs at once from CSV or TXT files |
| No resume if something goes wrong | Resumes from the exact chunk that failed — no re-billing |
| Manual download per job | Auto-saves audio + SRT subtitles to your output folder |
| No voice design tool | Design custom voices from text descriptions |

## Features

- **Unlimited text length** — automatically chunks long text and merges the audio seamlessly
- **Batch Generate** — process multiple jobs at once from a CSV or TXT file, sequential output naming (`01-Hook.mp3`, `02-Parte 1.mp3`)
- **Resume on failure** — if a job fails mid-way, only the failed chunks are retried — already completed chunks are not re-billed
- **My Jobs** — full history with waveform player, seek, SRT export, resume interrupted jobs
- **Batch Detail** — dedicated page per batch showing all jobs with play/save/status
- **Voice Library** — browse all voices, preview, filter by type
- **Voice Cloning** — upload audio samples and clone any voice
- **Voice Design** — describe a voice in text, generate previews, publish to library
- **SRT Subtitles** — word-level subtitle files saved automatically alongside every audio file
- **FFmpeg Integration** — auto-downloaded and managed, lossless batch merge into one file
- **Dashboard** — stats overview, 14-day usage chart, recent jobs
- **Dark / Light theme** — persisted preference
- **Windows notifications** — get notified when long jobs complete in the background

## Requirements

- Windows 10 or 11 (x64)
- [Inworld API key](https://platform.inworld.ai/)

## Installation

Download `FatimaTTS-v1.0.0-installer.msi` from the [latest release](https://github.com/hassanxs/fatima-tts/releases/latest) and run it.

The installer will:
- Install to `C:\Program Files\Fatima TTS\`
- Create a Start Menu shortcut
- Create a Desktop shortcut
- Register in Add/Remove Programs for clean uninstall

FFmpeg is downloaded automatically on first use (only needed for batch merge).

## Getting Started

1. Install and launch Fatima TTS
2. Go to **Settings** and paste your [Inworld API key](https://platform.inworld.ai/)
3. Click **Validate** to confirm the key works
4. Go to **Generate Speech** and start generating

Your API key is stored securely using Windows DPAPI — never written to disk in plaintext.

## Building from Source

```bash
git clone https://github.com/hassanxs/fatima-tts.git
cd fatima-tts
dotnet restore FatimaTTS/FatimaTTS.csproj
dotnet run --project FatimaTTS/FatimaTTS.csproj
```

### Publish self-contained exe

```powershell
dotnet publish FatimaTTS/FatimaTTS.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -o publish/
```

## Logs

Application logs are saved to:
```
%AppData%\FatimaTTS\logs\fatima_YYYY-MM-DD.log
```

Logs rotate daily and are pruned after 30 days. Open the logs folder from **Settings → Logs → Open Folder**.

## Tech Stack

- **WPF / .NET 8** — Windows Presentation Foundation, C#
- **NAudio** — audio playback and waveform extraction
- **Inworld TTS API** — synthesis, voice library, cloning, design
- **FFmpeg** — batch audio merging (auto-managed)
- **Windows DPAPI** — secure API key storage

## License

MIT — see [LICENSE](LICENSE)

## Contributing

Pull requests welcome. Please open an issue first to discuss significant changes.
