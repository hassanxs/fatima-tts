# Changelog

## v1.2.1

### Fixed
- The sidebar and the About page's "What's New" section were hardcoded to **v1.0.0**; both now read the real app version and show the current release's highlights.
- **Installer shortcuts** are now non-advertised and reference the app icon directly, fixing the blank/generic Start-Menu icon that advertised shortcuts caused.
- The installer now also creates a proper **Desktop shortcut** (with icon), as documented.

### Verify your download
```powershell
Get-FileHash "FatimaTTS-v1.2.1-installer.msi" -Algorithm SHA256
```
Compare the output with `SHA256SUMS.txt` attached to this release.

## v1.2.0

### Added
- **Parallel chunk synthesis** — long jobs now synthesize multiple chunks at once (configurable in Settings, default 3, capped to your Inworld plan's concurrency limit). Resume-on-failure and per-chunk retry are fully preserved.
- **Per-chunk regenerate** — re-roll a single chunk from the Generate page's chunk list without redoing the whole job; the output is re-merged automatically.
- **Pre-generation cost estimate** — the Generate page shows the estimated cost for the current text and model before you synthesize.
- **Usage & billing on the Dashboard** — estimated spend from characters actually billed by the API, plus a shortcut to the Inworld Portal usage page (Inworld has no usage API; the portal remains authoritative).
- **Configurable pricing** — per-model price-per-1M-characters in Settings, pre-filled with Inworld On-Demand rates, drives all cost figures.
- **Delivery Mode steering** — Stable / Balanced / Creative (best on Inworld TTS 2).
- **Language selection** — pick a BCP-47 language per generation, or leave it on the voice's default.
- **Text-normalization toggle** — control whether numbers, dates, and abbreviations are expanded.
- **Character-level captions** — choose Word- or Character-level timestamps; export either granularity as SRT (character data can produce both).
- **Update Voice** — edit a cloned voice's name, description, tags, gender, age group, and categories from the Voice Library.

### Changed
- Chunk splitting now targets a 1,900-character maximum (down from 2,000) for a safety margin against the hard API limit.

### Verify your download
```powershell
Get-FileHash "FatimaTTS-v1.2.0-installer.msi" -Algorithm SHA256
```
Compare the output with `SHA256SUMS.txt` attached to this release.

## v1.1.0

### Changed
- Updated the Inworld TTS model list to the current catalog:
  - Added the new flagship **Inworld TTS 2** (`inworld-tts-2`) — natural-language steering, ~120ms latency, 200+ languages — and made it the default for new installs.
  - Removed the discontinued `inworld-tts-1` and `inworld-tts-1-max` (retired by Inworld on 2026-06-15).
  - Current models: `inworld-tts-2`, `inworld-tts-1.5-max`, `inworld-tts-1.5-mini`.
- Older `settings.json` files that reference a removed model are now migrated automatically to its 1.5 successor; existing valid model choices are preserved.

### Verify your download
```powershell
Get-FileHash "FatimaTTS-v1.1.0-installer.msi" -Algorithm SHA256
```
Compare the output with `SHA256SUMS.txt` attached to this release.

## v1.0.0 — Initial Release

### Verify your download
Always verify the installer before running:
```powershell
Get-FileHash "FatimaTTS-v1.0.0-installer.msi" -Algorithm SHA256
```
Compare the output with `SHA256SUMS.txt` attached to this release.

### Features
- Generate Speech with chunked synthesis, per-chunk retry, and resume-on-failure
- Batch Generate from CSV/TXT files or manual queue — sequential output naming (01-Hook.mp3, 02-Parte 1.mp3)
- Batch Detail page — full job list per batch with play, save, resume, FFmpeg merge
- My Jobs — searchable history with persistent waveform player bar
- Voice Library — browse, preview, filter system and cloned voices
- Voice Cloning — upload audio samples, submit to Inworld clone API
- Voice Design — describe a voice, generate up to 3 previews, publish to library
- Dashboard — stats, 14-day usage chart, recent jobs
- SRT subtitle export — word-level timestamps, auto-saved alongside audio
- FFmpeg integration — auto-downloaded and managed, lossless batch merge
- Dark / Light theme with persistence
- Windows toast notifications on job completion
- File-based logging to %AppData%\FatimaTTS\logs\
- GitHub release update checker
