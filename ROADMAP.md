# Feature completion tracker

This file distinguishes implemented source features from features that still need implementation or verification. A feature is not production-verified until the Windows build and real-device tests pass.

## Implemented in source (requires validation)
- [x] Windows 11 x64 WPF/.NET 8 app and PerMonitorV2/long-path manifest.
- [x] Native `Windows.Graphics.Capture` system picker and `Direct3D11CaptureFramePool` frame path piped to FFmpeg.
- [x] FFmpeg `gdigrab` compatibility backend with monitor selection, window-title capture, and visual virtual-desktop region picker.
- [x] MP4/MKV output, quality presets, 30/60/120 FPS options, PNG screenshots.
- [x] Optional microphone and WASAPI desktop audio input; simultaneous inputs mixed into one AAC stream.
- [x] Optional fixed lower-right DirectShow webcam picture-in-picture overlay.
- [x] FFmpeg encoder discovery for NVENC/QSV/AMF/Media Foundation/libx264, with a built-in MPEG-4 fallback when the LGPL build has no usable H.264 encoder.
- [x] Pause/resume by process suspension; still needs long-recording/container stability testing.
- [x] Global start/stop, pause/resume, screenshot hotkeys; registration can fail if another app already owns a shortcut.
- [x] Recent recordings library with open, refresh, confirmed delete, and stream-copy trim/export.
- [x] Countdown and scheduled local-time start controls, plus cancellation.
- [x] Recovery marker and startup prompt to remux interrupted recordings into a new MKV while preserving the original.
- [x] Free-space guard: at least 1 GB before recording, clean-stop request below 256 MB during recording.
- [x] Graceful close prompt, saved settings, output folder controls, FFmpeg check, and audio-device listing.
- [x] Local opt-out error logging in `%LOCALAPPDATA%\NovaScreenRecorder\Error`; no uploads by the app.
- [x] Process arguments passed through `ProcessStartInfo.ArgumentList` for recording and screenshot capture.
- [x] xUnit utility tests and a Windows Graphics Capture API support smoke test, configured in CI.
- [x] Windows GitHub Actions build/publish, portable ZIP, FFmpeg LGPL notices, Inno Setup installer, and tag release upload. CI execution is still pending.

## Known limitations
- WGC frames are read back to CPU memory before FFmpeg input; this is not a zero-copy GPU pipeline and needs performance testing at high resolution/FPS.
- Webcam overlay width and corner position are configurable; separate microphone/system audio tracks and independent gain controls are not implemented.
- Stream-copy trim is keyframe-limited and is not a frame-accurate timeline editor.
- Recovery depends on the surviving media data and cannot recover frames that were never written.
- Installer is not code-signed; no signing certificate is configured.

## Release validation still required
- [ ] Successful GitHub Actions build, automated tests, publish, and installer compilation.
- [ ] Install, launch, upgrade, and uninstall tests on Windows 11 x64.
- [ ] Real-world tests across integrated/dedicated GPUs, mixed display scaling, multiple monitors, WGC window/display capture, webcams, microphones, WASAPI, sleep/resume, disk-full conditions, recovery, scheduling, trimming, and long recordings. See `docs-Windows-Hardware-Validation.md`.
- [ ] Functional tests for global shortcuts, pause/resume, stop/finalization, error logs, and file library actions.
