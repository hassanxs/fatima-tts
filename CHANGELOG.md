# Changelog

## v1.0.0 — Initial Release

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
