# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-11-12

### Added
- Windows tray app (C#/.NET 10, single self-contained `.exe`) built on yt-dlp, ffmpeg and whisper.cpp.
- Downloads from YouTube, Twitch, Kick, SoundCloud and every site yt-dlp supports, plus direct media links: video or audio only, offered automatically when a link is copied, or with <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>D</kbd>.
- Followed Twitch and Kick channels: new VODs are downloaded automatically after the stream ends; existing VODs are skipped when a channel is added; "Download the latest VOD" on demand.
- Subtitles with Whisper `large-v3-turbo` / `large-v3` on the GPU (CUDA build of whisper.cpp): `.srt` next to the file named for Jellyfin (`name.<lang>.srt`) and an embedded subtitle track, without re-encoding.
- Working folder on a fast disk: download, ffmpeg fixes, Whisper and the subtitle track all happen there, then the finished file is moved to its folder in one copy (useful when the media drive is slow or external). Video is always mp4.
- Separate folders for Twitch/Kick (one subfolder per streamer) and everything else; configurable yt-dlp file name templates, quality, audio format, cookies file, parallel downloads, free-space limit.
- Queue with progress in the tray menu, cancel, recent files, persistence across restarts; notifications; command line for the running instance; start with Windows; English and Italian.
- yt-dlp and ffmpeg installed on first launch and yt-dlp updated daily; Whisper installed the first time subtitles are needed.
