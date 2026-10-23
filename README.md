# Nova Screen Recorder

Nova Screen Recorder is a Windows 11 x64 desktop recording application built with C#/.NET 8 WPF and FFmpeg. Its visual identity is designed to accept the user's custom `NovaScreenRecorder-Icon.ico` artwork.

## Implemented in this source version

- Native Windows Graphics Capture (`Windows.Graphics.Capture`) display/window selection through the system picker, using `Direct3D11CaptureFramePool` and Win2D frame conversion into FFmpeg raw-video input.
- FFmpeg `gdigrab` compatibility mode with monitor enumeration, window-title capture, and a visual region-selection overlay that returns virtual-desktop physical-pixel coordinates (including negative coordinates for monitors left/above the primary display).
- MP4/MKV output, configurable quality preset, and 30/60/120 FPS selections. H.264 is preferred when an encoder is available; the final built-in fallback is MPEG-4 Part 2 for LGPL FFmpeg builds without a usable H.264 encoder.
- FFmpeg encoder discovery for NVIDIA NVENC, Intel Quick Sync, AMD AMF, Windows Media Foundation H.264, and `libx264` when present, with an encoder retry chain and built-in MPEG-4 fallback for LGPL builds that lack an H.264 encoder.
- Optional DirectShow microphone recording, with a helper to list DirectShow devices.
- Optional DirectShow webcam picture-in-picture overlay positioned in the lower-right corner; size/position is currently fixed.
- Optional WASAPI desktop audio; microphone + desktop audio can be mixed into one AAC output track when the selected FFmpeg build and devices support both.
- Pause/resume by suspending and resuming the FFmpeg process. This is not equivalent to a native capture-engine pause and should be tested with the chosen container/codec.
- PNG screenshots through native Windows Graphics Capture when that backend is selected, or through FFmpeg GDI compatibility capture.
- Choose/open output folder, elapsed timer, saved settings, FFmpeg path check, error messaging, and global Ctrl+Shift+R/P/S shortcuts (subject to system-wide shortcut conflicts).
- Recent recordings library for up to 100 videos/screenshots, with open and confirmed-delete actions.
- Disk safeguards: at least 1 GB free before recording; request a clean stop if free space falls below 256 MB during capture.
- Graceful close flow that asks to stop/finalize an active recording rather than immediately killing FFmpeg.
- Recovery sidecar markers for active recordings and a startup prompt to remux potentially interrupted files into a new MKV while preserving the original.
- Countdown and scheduled local-time start controls, with cancellation.
- FFmpeg stream-copy trim/export to a new file from the selected recording (cuts are keyframe-limited, not frame-accurate).
- xUnit helper tests and a Windows Graphics Capture API support smoke test, run in the Windows GitHub Actions build.
- Persistent local error diagnostics: exception details, stack traces, FFmpeg error output, timestamps, and recording context are written to daily logs in `%LOCALAPPDATA%\NovaScreenRecorder\Error`. Logs are retained until the user manually deletes them; the user can opt out of collecting new logs in Settings. Diagnostics are not uploaded.
- Windows manifest declares Windows 10/11 compatibility, per-monitor DPI awareness, and long-path support.
- GitHub Actions Windows build, self-contained .NET publish, FFmpeg LGPL build packaging, downloadable ZIP artifact, and tag-triggered GitHub Releases.
- Optional icon embedding when `src/NovaScreenRecorder.App/NovaScreenRecorder-Icon.ico` exists.

## Error folder and privacy

The app creates an `Error` folder at runtime under the current Windows user's Local AppData folder:

`%LOCALAPPDATA%\NovaScreenRecorder\Error`

Each day gets a `Nova-Error-YYYY-MM-DD.log` file. Entries include timestamps, error messages, exception type and stack trace where available, plus FFmpeg error output. The app does not upload logs. Logging is enabled by default; untick **Keep error logs on this PC** to stop collecting new entries. Existing logs are deliberately kept and can be manually deleted in File Explorer. There is no automatic age-based deletion, so logs remain available days or weeks later. Because logs can include file paths, review them before sharing with anyone.

## Windows 11 capture architecture and limitations

- Native capture uses the Windows `Windows.Graphics.Capture` API, its system picker, `Direct3D11CaptureFramePool`, and Win2D to convert frames into BGRA bytes for FFmpeg. Microsoft's API documentation explains the frame-pool and capture-session lifecycle: https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture. Win2D documents raw pixel access used by this pipeline: https://microsoft.github.io/Win2D/WinUI2/html/M_Microsoft_Graphics_Canvas_CanvasBitmap_GetPixelBytes.htm.
- Native WGC frames are copied from the GPU into CPU-readable pixel buffers before being piped to FFmpeg. This is a functional first implementation, not a zero-copy GPU pipeline; high-resolution/high-FPS capture can be CPU- and memory-bandwidth-intensive and needs benchmarking on the target PC.
- The native system picker selects a display or window. The compatibility backend provides a monitor list and a visual virtual-desktop region picker. Mixed-DPI coordinate behavior is designed for PerMonitorV2 and has automated coordinate tests, but still requires real mixed-DPI hardware validation.
- Webcam picture-in-picture currently supports a configurable overlay width and four corner positions. Separate microphone/system-audio tracks and richer source routing are not yet implemented.
- Automatic encoding probes FFmpeg's advertised encoders, prefers NVENC/QSV/AMF, then Media Foundation or `libx264`, and can fall back to FFmpeg's built-in MPEG-4 Part 2 encoder when the LGPL build has no usable H.264 encoder. An encoder being listed by FFmpeg does not guarantee that the installed GPU/driver can initialize it.
- Crash recovery preserves the original recording and attempts a stream-copy remux into a new MKV. Recovery depends on how much valid media data remains; it cannot restore frames that were never written.
- Trimming uses FFmpeg stream copy, so cuts can be limited to nearby keyframes. A frame-accurate editor would need re-encoding and a timeline UI.
- CI can build and run deterministic tests, but it cannot certify all GPU, camera, audio, multi-monitor, or sleep/resume combinations. See `docs-Windows-Hardware-Validation.md` and complete the checklist on the actual Windows 11 PC before production use.

## Honest status

This repository now includes the major requested source features, but it is **not yet verified as a production-ready release**. The WGC path, FFmpeg input/encoder combinations, installer, recovery, scheduled starts, and webcam overlay still need a successful Windows build plus real-device testing. Do not assume all hardware configurations will work merely because the source has been added.

## Requirements

- Windows 11 x64.
- The portable ZIP produced by GitHub Actions includes the app folder, self-contained Windows App SDK runtime files, and an FFmpeg LGPL build, subject to the third-party notice/license files included by the build workflow. The application EXE is not a standalone single-file binary; use the installer EXE or keep all files in the extracted folder together.
- To build from source: .NET 8 SDK and Visual Studio Build Tools with the Windows desktop workload.

## Build locally

```powershell
dotnet restore .\NovaScreenRecorder.sln
dotnet build .\NovaScreenRecorder.sln -c Release
```

Publish locally:

```powershell
.\scripts\Publish.ps1
```

## GitHub download steps

1. Push the repository to GitHub.
2. Open **Actions** and choose the latest successful **Build Nova Screen Recorder** run.
3. Download the `NovaScreenRecorder-win-x64` artifact. It includes the portable ZIP and `NovaScreenRecorder-Setup-win-x64.exe`. The app executable depends on its adjacent runtime files, so do not copy the EXE out of the folder by itself.
4. For the portable build, extract the ZIP and keep all files together in the extracted folder (the FFmpeg binary may need its adjacent DLLs). For the installer, run the Setup EXE and follow the prompts.
5. For a permanent Release, push a tag such as `v0.1.0`; the workflow attaches the ZIP and Setup EXE to the GitHub Release.

## Custom icon

Save the finished Windows icon as:

`src/NovaScreenRecorder.App/NovaScreenRecorder-Icon.ico`

Commit it and the next successful workflow run will embed it into the executable. A `.png` by itself cannot be embedded as the Windows executable icon; export/convert the design to `.ico`.

## License

The Nova application source is licensed under MIT. FFmpeg is a separate third-party project under its own licensing terms; review and retain the applicable notices in any redistributed package.


## Expanded workflow and reliability features

- **Global shortcuts:** `Ctrl+Shift+R` toggles start/stop, `Ctrl+Shift+P` pauses/resumes, and `Ctrl+Shift+S` requests a screenshot. Windows may reject a shortcut if another application already registered it; Nova writes a diagnostic warning and retains focused-window shortcuts.
- **Mixed audio:** microphone and desktop loopback can be enabled together. FFmpeg mixes both tracks into a single AAC stream; actual device names and WASAPI support depend on the local Windows/FFmpeg setup.
- **Native WGC:** the system picker selects a display/window and WGC frame-pool frames are piped to FFmpeg as BGRA raw video. This has not yet been benchmarked across real GPUs or HDR setups.
- **Monitor/region selection:** compatibility capture enumerates monitors and the visual region overlay returns virtual-desktop physical coordinates, including negative origins.
- **Hardware encoding:** FFmpeg encoder discovery supports automatic NVENC/QSV/AMF preference and CPU fallback; actual GPU driver compatibility must be tested locally.
- **Recovery, trimming, schedule:** interrupted recordings receive a recovery prompt, trim/export writes a separate file, and countdown/scheduled start are available. Stream-copy trimming is keyframe-limited.
- **Recordings library:** the app lists up to 100 newest videos and screenshots from the selected output folder. Double-click/open selected launches the file with its default Windows app; deletion requires confirmation and the active recording cannot be deleted.
- **Storage safeguards:** recording startup requires at least 1 GB free. During recording, Nova checks free space every 10 seconds and requests a clean stop if free space falls below 256 MB. These are safeguards, not a guarantee against sudden disk failures.
- **Safer process arguments:** recording and screenshot FFmpeg arguments are passed through `ProcessStartInfo.ArgumentList` rather than manually quoted command-line strings, which is safer for paths and window titles containing spaces or punctuation.

## Remaining validation and limitations

The source implementation and automated test suite must pass on the Windows GitHub Actions runner. Then complete the real-device checklist in `docs-Windows-Hardware-Validation.md`. Pause still suspends the FFmpeg process rather than pausing the capture API itself. WGC currently transfers frame pixels through CPU memory, the webcam overlay supports width/corner settings but still needs performance testing, trim cuts are keyframe-limited, and no code-signing certificate is configured. The installer is unsigned unless the owner adds a legitimate signing workflow.


## Windows hardware validation

Follow `docs-Windows-Hardware-Validation.md` for the actual Windows 11 device test plan and run `scripts/Test-Windows11-Hardware.ps1` to gather local diagnostics. CI cannot prove that the selected GPU driver, webcams, audio devices, mixed-DPI monitors, or display/window capture permissions work on your PC.

## Windows installer

The Windows GitHub Actions workflow now compiles an Inno Setup installer as `NovaScreenRecorder-Setup-win-x64.exe`, alongside the portable ZIP. The installer targets 64-bit Windows and installs to Program Files; it offers an optional desktop shortcut. The workflow also collects FFmpeg LGPL license/notice files alongside the bundled executable. The installer and its build have not yet been executed in this environment, so the first GitHub Actions run must confirm the installer packaging step succeeds.
