# Changelog

## Expanded native-capture and workflow implementation

- Added a native Windows Graphics Capture backend using the system picker, `Direct3D11CaptureFramePool`, and Win2D BGRA frame transfer to FFmpeg.
- Added native WGC PNG screenshots and kept FFmpeg GDI capture as the compatibility backend.
- Added a virtual-desktop region overlay, monitor enumeration, and an explicit all-displays/selected-monitor/window/region capture mode.
- Added configurable webcam picture-in-picture width and four-corner positioning.
- Added FFmpeg encoder discovery, ordered fallback across NVENC/QSV/AMF/Media Foundation/libx264, and built-in MPEG-4 fallback for LGPL builds without a usable H.264 encoder.
- Added recovery sidecars and a startup remux-recovery prompt for interrupted recordings.
- Added countdown and scheduled recording, stream-copy trim/export, and local Windows hardware-report script.
- Added xUnit helper tests, a Windows Graphics Capture API support smoke test, and a synthetic FFmpeg audio/video smoke test in CI.
- Added Windows App SDK self-contained deployment settings; publishing is intentionally folder-based rather than single-file because native runtime dependencies must ship alongside the app.

## Verification status

Static XML/XAML/handler/brace checks passed in the editing environment. A .NET SDK, Windows desktop session, and physical Windows 11 capture hardware were not available here, so the solution has not been compiled or exercised end-to-end in this environment. The first successful Windows GitHub Actions run and the checklist in `docs-Windows-Hardware-Validation.md` remain release gates.
