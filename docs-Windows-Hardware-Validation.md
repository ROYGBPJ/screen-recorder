# Nova Screen Recorder — Windows 11 hardware validation

Automated CI compiles the app and runs deterministic helper tests on a Windows runner. A GitHub-hosted runner is not a substitute for validating a real user's GPU, microphone, webcam, display topology, DPI mix, or audio drivers. Complete this checklist on the target Windows 11 PC before calling a release production-ready. Run `powershell -ExecutionPolicy Bypass -File .\scripts\Test-Windows11-Hardware.ps1` from the repository to collect a local report of Windows build, display bounds, FFmpeg encoders/devices, and automated test output.

## Capture and displays

- [ ] Select each display with the native Windows Graphics Capture picker; record 2 minutes at 30 and 60 FPS.
- [ ] Select a window; minimize/restore it and verify capture behavior is understandable and the output is playable.
- [ ] Test a primary 100% DPI display and a secondary display at 125%, 150%, or 200% DPI.
- [ ] Test a monitor positioned left of or above the primary display; verify the visual region picker's negative virtual-desktop coordinates.
- [ ] Resize a selected display/window during capture and verify frame dimensions remain stable and video remains playable.
- [ ] Compare native WGC and compatibility GDI capture for cursor, protected surfaces, HDR, fullscreen applications, and GPU load.

## Video and hardware encoding

- [ ] Run encoder detection and record using Automatic, CPU, NVIDIA NVENC (if present), Intel Quick Sync (if present), and AMD AMF (if present).
- [ ] Force an unavailable hardware encoder and verify the UI reports a safe CPU fallback.
- [ ] Test 30/60/120 FPS at 1080p and at the maximum intended display resolution; check dropped frames, CPU/GPU use, file playback, and audio sync.
- [ ] Test MP4 and MKV, normal stop, forced FFmpeg termination, and app crash/relaunch recovery. Preserve the original damaged file.

## Audio and webcam

- [ ] Test microphone-only, desktop loopback-only, and simultaneous audio mixing with headphones and speakers.
- [ ] Test webcam overlay with multiple device names, device already in use, unplug/replug, and 720p/1080p sources.
- [ ] Confirm audio/video sync over a 10-minute recording.

## Workflow and safety

- [ ] Test all global hotkeys when Nova is unfocused and when another app already owns one of them.
- [ ] Test countdown cancellation, scheduled recording, sleep/resume, and system clock/time-zone changes.
- [ ] Trim a file with keyframes far apart; verify stream-copy trim accuracy is acceptable (cuts may be keyframe-limited).
- [ ] Test free-space warning and automatic stop on a disposable/test drive; never intentionally fill the system drive.
- [ ] Verify error logs stay local, persist across relaunch, and stop collecting when diagnostics are disabled.
- [ ] Install, launch, uninstall, reinstall, and confirm user recordings are not removed by uninstall.

## Release gate

Do not label a release fully verified until all applicable items are checked on the intended Windows 11 hardware and the exact build's CI test/build/installer artifacts are green.
