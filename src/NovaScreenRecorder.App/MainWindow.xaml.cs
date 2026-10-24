using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Text.Json;
using System.Text;
using System.Drawing;
using Windows.Graphics.Capture;

namespace NovaScreenRecorder.App;

public partial class MainWindow : Window
{
    private Process? _ffmpeg;
    private DateTime _recordingStarted;
    private TimeSpan _pausedElapsed = TimeSpan.Zero;
    private bool _isPaused;
    private string? _currentOutput;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Nova Recordings");
    private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaScreenRecorder", "settings.txt");
    private bool _diagnosticsEnabled = true;
    private bool _isStopping;
    private bool _recordingReady;
    private bool _lastFfmpegInputFailure;
    private bool _allowClose;
    private string? _stopWarning;
    private HwndSource? _windowSource;
    private const int HotkeyStartStop = 0x4A01;
    private const int HotkeyPause = 0x4A02;
    private const int HotkeyScreenshot = 0x4A03;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const long MinimumFreeBytes = 1L * 1024 * 1024 * 1024;
    private const long EmergencyFreeBytes = 256L * 1024 * 1024;
    private DateTime _lastDiskCheck = DateTime.MinValue;
    private readonly WindowsGraphicsCaptureService _wgc = new();
    private bool _isWgcRecording;
    private string _activeEncoder = "libx264";
    private readonly HashSet<string> _failedEncoders = new(StringComparer.OrdinalIgnoreCase);
    private bool _automaticEncoder = true;
    private readonly HashSet<string> _availableEncoders = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _scheduleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _scheduledStart;
    private CancellationTokenSource? _countdownCancellation;
    private string? _recoveryMarker;
    private WasapiLoopbackRecorder? _desktopAudioRecorder;
    private string? _desktopAudioWav;
    private bool _recordingIncludesMicrophone;

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr processHandle);

    public MainWindow()
    {
        InitializeComponent();
        var bundledFfmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        FfmpegPathBox.Text = File.Exists(bundledFfmpeg) ? bundledFfmpeg : "ffmpeg.exe";
        Directory.CreateDirectory(_outputFolder);
        ErrorLogService.Initialize(true);
        OutputPathText.Text = _outputFolder;
        _timer.Tick += (_, _) => UpdateElapsed();
        _scheduleTimer.Tick += async (_, _) =>
        {
            if (_scheduledStart is null) { _scheduleTimer.Stop(); return; }
            var remaining = _scheduledStart.Value - DateTime.Now;
            if (remaining <= TimeSpan.Zero) { _scheduleTimer.Stop(); _scheduledStart = null; ScheduleCheckBox.IsChecked = false; MessageText.Text = "Scheduled recording starting…"; await StartRecordingAsync(true); }
            else MessageText.Text = $"Scheduled recording in {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}.";
        };
        _wgc.CaptureError += ex => { ErrorLogService.WriteException("Windows Graphics Capture frame stream", ex); Dispatcher.BeginInvoke(new Action(() => MessageText.Text = $"Native capture warning: {ex.Message}")); };
        Loaded += async (_, _) =>
        {
            LoadSettings();
            RefreshMonitors();
            RefreshRecordingsLibrary();
            RecoverPendingRecordings();
            // Populate the device dropdowns automatically on each launch.
            await LoadDirectShowDevicesAsync(showMessage: false);
        };
        SourceInitialized += (_, _) => RegisterGlobalHotkeys();
        Closing += async (_, e) =>
        {
            if (!_allowClose && _ffmpeg is { HasExited: false })
            {
                e.Cancel = true;
                var answer = System.Windows.MessageBox.Show(this,
                    "A recording is still active. Stop and finalize the file before closing Nova?",
                    "Recording in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes)
                {
                    await StopRecordingAsync();
                    _allowClose = true;
                    Close();
                }
                return;
            }
            _scheduleTimer.Stop(); _countdownCancellation?.Cancel();
            UnregisterGlobalHotkeys();
            try { _wgc.Dispose(); } catch { }
            SaveSettings();
        };
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var lines = File.ReadAllLines(SettingsPath);
            if (lines.Length > 0 && Directory.Exists(lines[0])) _outputFolder = lines[0];
            if (lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1])) FfmpegPathBox.Text = lines[1];
            if (lines.Length > 2) MicDeviceBox.Text = lines[2];
            if (lines.Length > 3 && bool.TryParse(lines[3], out bool enabled)) _diagnosticsEnabled = enabled;
            if (lines.Length > 4) WebcamDeviceBox.Text = lines[4];
            if (lines.Length > 5 && int.TryParse(lines[5], out int webcamWidth) && webcamWidth >= 64 && webcamWidth <= 1280) WebcamWidthBox.Text = webcamWidth.ToString();
            if (lines.Length > 6 && int.TryParse(lines[6], out int webcamPosition) && webcamPosition >= 0 && webcamPosition < WebcamPositionCombo.Items.Count) WebcamPositionCombo.SelectedIndex = webcamPosition;
            if (lines.Length > 7 && !string.IsNullOrWhiteSpace(lines[7])) DesktopAudioDeviceBox.Text = lines[7];
            ErrorLogService.Initialize(_diagnosticsEnabled);
            if (DiagnosticsCheckBox is not null) DiagnosticsCheckBox.IsChecked = _diagnosticsEnabled;
            OutputPathText.Text = _outputFolder;
        }
        catch (Exception ex) { ErrorLogService.WriteException("Load settings", ex); }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            _diagnosticsEnabled = DiagnosticsCheckBox?.IsChecked != false;
            ErrorLogService.Enabled = _diagnosticsEnabled;
            File.WriteAllLines(SettingsPath, new[] { _outputFolder, FfmpegPathBox.Text.Trim(), MicDeviceBox.Text.Trim(), _diagnosticsEnabled.ToString(), WebcamDeviceBox.Text.Trim(), WebcamWidthBox.Text.Trim(), WebcamPositionCombo.SelectedIndex.ToString(), DesktopAudioDeviceBox.Text.Trim() });
        }
        catch (Exception ex) { ErrorLogService.WriteException("Save settings", ex); }
    }

    private string FfmpegPath
    {
        get
        {
            var value = FfmpegPathBox.Text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value) || value.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            {
                var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
                return File.Exists(bundled) ? bundled : "ffmpeg.exe";
            }
            return value;
        }
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose where Nova saves recordings", SelectedPath = _outputFolder, UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _outputFolder = dialog.SelectedPath; OutputPathText.Text = _outputFolder; SaveSettings();
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_outputFolder); Process.Start(new ProcessStartInfo("explorer.exe", _outputFolder) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError($"Could not open output folder: {ex.Message}"); }
    }

    private void CaptureModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowTitleBox is null) return;
        bool window = CaptureModeCombo.SelectedIndex == 2;
        bool region = CaptureModeCombo.SelectedIndex == 3;
        WindowTitleBox.IsEnabled = window;
        RegionXBox.IsEnabled = RegionYBox.IsEnabled = RegionWidthBox.IsEnabled = RegionHeightBox.IsEnabled = region;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartRecordingAsync();

    private async Task StartRecordingAsync(bool bypassCountdown = false, bool encoderRetry = false)
    {
        if (_ffmpeg is { HasExited: false }) return;
        if (!encoderRetry) _failedEncoders.Clear();
        if (!bypassCountdown && ScheduleCheckBox.IsChecked == true)
        {
            if (ScheduleDatePicker.SelectedDate is null || !TimeSpan.TryParseExact(ScheduleTimeBox.Text.Trim(), @"hh\:mm", null, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            { ShowError("Choose a schedule date and enter a local time in HH:mm format."); return; }
            var when = ScheduleDatePicker.SelectedDate.Value.Date + time;
            if (when <= DateTime.Now) { ShowError("The scheduled time must be in the future."); return; }
            _scheduledStart = when; _scheduleTimer.Start(); MessageText.Text = $"Recording scheduled for {when:yyyy-MM-dd HH:mm:ss}."; return;
        }
        if (!bypassCountdown && !string.IsNullOrWhiteSpace(CountdownSecondsBox.Text))
        {
            if (!int.TryParse(CountdownSecondsBox.Text, out int countdown) || countdown < 0 || countdown > 600)
            { ShowError("Countdown must be a whole number between 0 and 600 seconds."); return; }
            if (countdown > 0)
            {
            _countdownCancellation?.Cancel(); _countdownCancellation = new CancellationTokenSource();
            var token = _countdownCancellation.Token; StartButton.IsEnabled = false;
            try { for (int remaining = countdown; remaining > 0; remaining--) { MessageText.Text = $"Recording starts in {remaining}…"; await Task.Delay(1000, token); } }
            catch (OperationCanceledException) { MessageText.Text = "Countdown cancelled."; StartButton.IsEnabled = true; return; }
            finally { _countdownCancellation = null; }
            StartButton.IsEnabled = true;
            await StartRecordingAsync(true); return;
            }
        }
        if (!EnsureFfmpeg()) return;
        if (CaptureBackendCombo.SelectedIndex == 0 && !WindowsGraphicsCaptureService.IsSupported)
        { ShowError("Windows Graphics Capture is not supported on this Windows build. Choose FFmpeg GDI compatibility capture instead."); return; }
        if (CaptureBackendCombo.SelectedIndex == 0 && _wgc.SelectedItem is null)
        {
            // Open the native picker automatically if Start is pressed before choosing a target.
            try
            {
                var picked = await _wgc.PickItemAsync(new WindowInteropHelper(this).Handle);
                SelectedCaptureText.Text = picked && _wgc.SelectedItem is not null
                    ? $"Native capture target selected • {_wgc.SelectedItem.Size.Width} × {_wgc.SelectedItem.Size.Height}"
                    : "Native capture target: selection cancelled";
                if (!picked || _wgc.SelectedItem is null)
                { ShowError("Select a display or window in the Windows capture picker to start recording."); return; }
            }
            catch (Exception ex)
            {
                ErrorLogService.WriteException("Open native capture picker at recording start", ex);
                ShowError($"Could not open the Windows capture picker: {ex.Message}");
                return;
            }
        }
        await DetectEncodersAsync();
        if (MicCheckBox.IsChecked == true && string.IsNullOrWhiteSpace(MicDeviceBox.Text.Trim().Trim('"')))
        {
            ShowError("Enter the exact DirectShow microphone device name, or turn microphone recording off."); return;
        }
        // Desktop audio is captured separately through Windows WASAPI loopback and muxed
        // after the video file is cleanly finalized. FFmpeg DirectShow cannot reliably
        // capture Windows playback endpoints.
        if (CaptureBackendCombo.SelectedIndex != 0 && CaptureModeCombo.SelectedIndex == 2 && string.IsNullOrWhiteSpace(WindowTitleBox.Text))
        {
            ShowError("Enter the visible title of the window you want to record."); return;
        }

        var extension = FormatCombo.SelectedIndex == 1 ? "mkv" : "mp4";
        try
        {
            Directory.CreateDirectory(_outputFolder);
            var driveRoot = Path.GetPathRoot(Path.GetFullPath(_outputFolder));
            if (!string.IsNullOrEmpty(driveRoot))
            {
                var drive = new DriveInfo(driveRoot);
                if (drive.IsReady && drive.AvailableFreeSpace < MinimumFreeBytes)
                {
                    ShowError($"Not enough free disk space. Nova requires at least 1 GB free; this drive has {drive.AvailableFreeSpace / (1024d * 1024 * 1024):F2} GB available.");
                    return;
                }
            }
        }
        catch (Exception ex) { ErrorLogService.WriteException("Check output storage", ex); ShowError($"Cannot use the recording folder: {ex.Message}"); return; }
        _currentOutput = Path.Combine(_outputFolder, $"Nova_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{extension}");
        if (File.Exists(_currentOutput)) _currentOutput = Path.Combine(_outputFolder, $"Nova_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.{extension}");
        var fps = SelectedFps();
        _isWgcRecording = CaptureBackendCombo.SelectedIndex == 0;
        var args = new List<string> { "-hide_banner", "-y" };
        int sourceWidth = 0, sourceHeight = 0;
        if (_isWgcRecording)
        {
            var item = _wgc.SelectedItem!;
            sourceWidth = item.Size.Width; sourceHeight = item.Size.Height;
            args.AddRange(new[] { "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{sourceWidth}x{sourceHeight}", "-framerate", fps.ToString(), "-i", "pipe:0" });
        }
        else
        {
            args.AddRange(new[] { "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", CursorCheckBox.IsChecked == true ? "1" : "0" });
            if (CaptureModeCombo.SelectedIndex == 2) args.AddRange(new[] { "-i", $"title={WindowTitleBox.Text.Trim()}" });
            else if (CaptureModeCombo.SelectedIndex == 3)
            {
                if (!int.TryParse(RegionXBox.Text, out int x) || !int.TryParse(RegionYBox.Text, out int y) || !int.TryParse(RegionWidthBox.Text, out int w) || !int.TryParse(RegionHeightBox.Text, out int h) || w < 16 || h < 16)
                { ShowError("Region values must be valid integers; width and height must be at least 16 pixels."); return; }
                args.AddRange(new[] { "-offset_x", x.ToString(), "-offset_y", y.ToString(), "-video_size", $"{w}x{h}", "-i", "desktop" });
                sourceWidth = w; sourceHeight = h;
            }
            else if (CaptureModeCombo.SelectedIndex == 1 && MonitorCombo.SelectedItem is MonitorChoice monitor)
            {
                args.AddRange(new[] { "-offset_x", monitor.Bounds.X.ToString(), "-offset_y", monitor.Bounds.Y.ToString(), "-video_size", $"{monitor.Bounds.Width}x{monitor.Bounds.Height}", "-i", "desktop" });
                sourceWidth = monitor.Bounds.Width; sourceHeight = monitor.Bounds.Height;
            }
            else args.AddRange(new[] { "-i", "desktop" });
        }

        bool mic = MicCheckBox.IsChecked == true;
        bool desktopAudio = DesktopAudioCheckBox.IsChecked == true;
        bool webcam = WebcamCheckBox.IsChecked == true;
        if (webcam && string.IsNullOrWhiteSpace(WebcamDeviceBox.Text)) { ShowError("Enter the exact DirectShow webcam device name, or turn webcam overlay off."); return; }
        int webcamWidth = 320;
        if (webcam && (!int.TryParse(WebcamWidthBox.Text, out webcamWidth) || webcamWidth < 64 || webcamWidth > 1280)) { ShowError("Webcam overlay width must be between 64 and 1280 pixels."); return; }
        int nextInput = 1;
        int? webcamInput = null;
        // ProcessStartInfo.ArgumentList already preserves each argument as one token.
        // Do not include shell-style quotes inside DirectShow input values: FFmpeg then
        // treats the quote characters as part of the device name and cannot open it.
        if (webcam) { webcamInput = nextInput++; args.AddRange(new[] { "-f", "dshow", "-i", BuildDirectShowInput("video", WebcamDeviceBox.Text) }); }
        int? micInput = null;
        if (mic) { micInput = nextInput++; args.AddRange(new[] { "-f", "dshow", "-i", BuildDirectShowInput("audio", MicDeviceBox.Text) }); }
        var filterParts = new List<string>();
        var outputMaps = new List<string>();
        if (webcam)
        {
            var overlayPosition = WebcamPositionCombo.SelectedIndex switch { 1 => "20:H-h-20", 2 => "W-w-20:20", 3 => "20:20", _ => "W-w-20:H-h-20" };
            filterParts.Add($"[{webcamInput}:v:0]scale={webcamWidth}:-2[cam];[0:v:0][cam]overlay={overlayPosition}[vout]");
            outputMaps.AddRange(new[] { "-map", "[vout]" });
        }
        else outputMaps.AddRange(new[] { "-map", "0:v:0" });
        if (mic) outputMaps.AddRange(new[] { "-map", $"{micInput}:a:0" });
        else outputMaps.Add("-an");
        if (filterParts.Count > 0) args.AddRange(new[] { "-filter_complex", string.Join(";", filterParts) });
        args.AddRange(outputMaps);

        _activeEncoder = SelectEncoder();
        args.AddRange(new[] { "-c:v", _activeEncoder, "-pix_fmt", "yuv420p" });
        if (_activeEncoder == "libx264") args.AddRange(new[] { "-preset", SelectedPreset(), "-crf", SelectedCrf().ToString() });
        else if (_activeEncoder == "h264_nvenc") args.AddRange(new[] { "-preset", "p5", "-rc", "vbr", "-cq", SelectedCrf().ToString(), "-b:v", "0" });
        else if (_activeEncoder == "h264_qsv") args.AddRange(new[] { "-preset", "medium", "-global_quality", SelectedCrf().ToString() });
        else if (_activeEncoder == "h264_amf") args.AddRange(new[] { "-quality", "balanced", "-qp_i", SelectedCrf().ToString(), "-qp_p", SelectedCrf().ToString() });
        else if (_activeEncoder == "h264_mf") args.AddRange(new[] { "-b:v", SelectedBitrate() });
        else if (_activeEncoder == "mpeg4") args.AddRange(new[] { "-q:v", SelectedMpeg4Quality().ToString() });
        args.AddRange(new[] { "-r", fps.ToString() });
        if (mic) args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        if (extension == "mp4") args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(_currentOutput);

        try
        {
            _lastFfmpegInputFailure = false;
            var startInfo = CreateProcessStartInfo(args, redirectInput: true, redirectOutput: true, redirectError: true);
            _ffmpeg = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var ffmpegDiagnostic = new StringBuilder();
            var ffmpegDiagnosticGate = new object();
            var ffmpegStderrClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _isStopping = false;
            _recordingReady = false;
            _stopWarning = null;
            _ffmpeg.ErrorDataReceived += (_, ev) =>
            {
                if (ev.Data is null) { ffmpegStderrClosed.TrySetResult(true); return; }
                lock (ffmpegDiagnosticGate) ffmpegDiagnostic.AppendLine(ev.Data);
                if (ev.Data.Contains("Error opening input", StringComparison.OrdinalIgnoreCase) ||
                    ev.Data.Contains("Could not open input", StringComparison.OrdinalIgnoreCase) ||
                    ev.Data.Contains("Could not find audio device", StringComparison.OrdinalIgnoreCase) ||
                    ev.Data.Contains("No such device", StringComparison.OrdinalIgnoreCase))
                    _lastFfmpegInputFailure = true;
                if (ev.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    ev.Data.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    ev.Data.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                    ev.Data.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
                    ev.Data.Contains("not found", StringComparison.OrdinalIgnoreCase))
                {
                    ErrorLogService.Write("FFMPEG", $"Output: {ev.Data}{Environment.NewLine}Output file: {_currentOutput ?? "(not set)"}");
                    Dispatcher.BeginInvoke(new Action(() => MessageText.Text = ev.Data));
                }
            };
            _ffmpeg.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() => { if (_recordingReady) HandleUnexpectedExit(); }));
            _ffmpeg.Start(); _ffmpeg.BeginErrorReadLine(); _ffmpeg.BeginOutputReadLine();
            if (_isWgcRecording) _wgc.Start(_ffmpeg.StandardInput.BaseStream, fps, CursorCheckBox.IsChecked == true);
            _recoveryMarker = _currentOutput + ".nova-recovery.json";
            File.WriteAllText(_recoveryMarker, JsonSerializer.Serialize(new RecoveryMarker(_currentOutput, _ffmpeg.Id, DateTime.UtcNow, _activeEncoder)));
            await Task.Delay(700);
            if (_ffmpeg.HasExited)
            {
                var failedEncoder = _activeEncoder;
                // Let the redirected stderr reader drain so the Error log contains FFmpeg's
                // actual reason for exiting, not just the generic startup-failure message.
                await Task.WhenAny(ffmpegStderrClosed.Task, Task.Delay(500));
                string ffmpegErrorText;
                lock (ffmpegDiagnosticGate) ffmpegErrorText = ffmpegDiagnostic.ToString();
                int ffmpegExitCode = _ffmpeg.ExitCode;
                ErrorLogService.Write("FFMPEG START FAILURE",
                    $"Exit code: {ffmpegExitCode}{Environment.NewLine}Encoder: {failedEncoder}{Environment.NewLine}Output file: {_currentOutput ?? "(not set)"}{Environment.NewLine}Arguments: {string.Join(" ", args)}{Environment.NewLine}FFmpeg stderr:{Environment.NewLine}{(string.IsNullOrWhiteSpace(ffmpegErrorText) ? "(FFmpeg produced no stderr text.)" : ffmpegErrorText)}");
                _ffmpeg.Dispose(); _ffmpeg = null;
                if (_isWgcRecording) { try { await _wgc.StopAsync(); } catch { } }
                if (_recoveryMarker is not null) { try { File.Delete(_recoveryMarker); } catch { } _recoveryMarker = null; }
                // This file was created by this failed startup and never became a recording.
                // Remove the broken container instead of leaving an MP4 that looks usable.
                try { if (_currentOutput is not null && File.Exists(_currentOutput)) File.Delete(_currentOutput); }
                catch (Exception ex) { ErrorLogService.WriteException("Remove failed startup output", ex, _currentOutput); }
                if (_lastFfmpegInputFailure && !encoderRetry)
                {
                    bool microphoneFailed = MicCheckBox.IsChecked == true &&
                        !string.IsNullOrWhiteSpace(MicDeviceBox.Text) &&
                        ffmpegErrorText.Contains(MicDeviceBox.Text.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
                    bool webcamFailed = WebcamCheckBox.IsChecked == true &&
                        !string.IsNullOrWhiteSpace(WebcamDeviceBox.Text) &&
                        ffmpegErrorText.Contains(WebcamDeviceBox.Text.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
                    if (microphoneFailed)
                    {
                        MicCheckBox.IsChecked = false;
                        var fallbackWarning = "The selected microphone could not be opened. Nova is retrying without microphone input; desktop loopback audio can still be recorded.";
                        await StartRecordingAsync(true, true);
                        if (_recordingReady) { _stopWarning = fallbackWarning; MessageText.Text += Environment.NewLine + fallbackWarning; }
                        return;
                    }
                    if (webcamFailed)
                    {
                        WebcamCheckBox.IsChecked = false;
                        var fallbackWarning = "The selected webcam could not be opened. Nova is retrying without the webcam overlay.";
                        await StartRecordingAsync(true, true);
                        if (_recordingReady) { _stopWarning = fallbackWarning; MessageText.Text += Environment.NewLine + fallbackWarning; }
                        return;
                    }
                }
                if (!_lastFfmpegInputFailure && _automaticEncoder && failedEncoder != "mpeg4" && _failedEncoders.Add(failedEncoder))
                {
                    MessageText.Text = $"Encoder {failedEncoder} failed to initialize; retrying with the next available encoder.";
                    await StartRecordingAsync(true, true); return;
                }
                ShowError("FFmpeg stopped immediately. Check the Error folder, selected audio/webcam devices, and FFmpeg encoder support."); return;
            }
            if (desktopAudio)
            {
                // Keep the temporary loopback WAV out of the recordings folder. It is an
                // intermediate track only; the finished recording should contain the audio.
                var audioTempFolder = Path.Combine(Path.GetTempPath(), "NovaScreenRecorder", "Audio");
                Directory.CreateDirectory(audioTempFolder);
                _desktopAudioWav = Path.Combine(audioTempFolder, $"{Guid.NewGuid():N}.desktop-audio.wav");
                try
                {
                    _desktopAudioRecorder = new WasapiLoopbackRecorder(_desktopAudioWav);
                    await _desktopAudioRecorder.StartAsync();
                }
                catch (Exception audioEx)
                {
                    ErrorLogService.WriteException("Start WASAPI desktop audio", audioEx, _desktopAudioWav);
                    try { _desktopAudioRecorder?.Dispose(); } catch { }
                    _desktopAudioRecorder = null;
                    try { if (File.Exists(_desktopAudioWav)) File.Delete(_desktopAudioWav); } catch { }
                    _desktopAudioWav = null;
                    _stopWarning = $"Desktop audio could not start; video recording will continue without it. Details: {audioEx.Message}";
                }
            }
            _recordingIncludesMicrophone = mic;
            _recordingReady = true;
            _recordingStarted = DateTime.Now; _pausedElapsed = TimeSpan.Zero; _isPaused = false; _timer.Start();
            StatusText.Text = "RECORDING"; StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed; MessageText.Text = $"Recording to: {_currentOutput}\nEncoder: {_activeEncoder} • Backend: {(_isWgcRecording ? "Windows Graphics Capture" : "FFmpeg GDI compatibility")}";
            StartButton.IsEnabled = false; PauseButton.IsEnabled = true; StopButton.IsEnabled = true; ScreenshotButton.IsEnabled = false; ChooseFolderButton.IsEnabled = false;
            SetConfigurationEnabled(false); PauseButton.Content = "Ⅱ  Pause"; SaveSettings();
            RefreshRecordingsLibrary();
        }
        catch (Exception ex)
        {
            ErrorLogService.WriteException("Start recording", ex, $"FFmpeg={FfmpegPath}; output={_currentOutput}");
            try { if (_desktopAudioRecorder is not null) await _desktopAudioRecorder.StopAsync(); } catch { }
            try { _desktopAudioRecorder?.Dispose(); } catch { }
            _desktopAudioRecorder = null; _desktopAudioWav = null;
            try { if (_ffmpeg is { HasExited: false }) _ffmpeg.Kill(true); } catch { }
            try { _ffmpeg?.Dispose(); } catch { }
            _ffmpeg = null; _recordingReady = false;
            if (_isWgcRecording) { try { await _wgc.StopAsync(); } catch { } }
            _isWgcRecording = false;
            if (_recoveryMarker is not null) { try { File.Delete(_recoveryMarker); } catch { } _recoveryMarker = null; }
            try { if (_currentOutput is not null && File.Exists(_currentOutput)) File.Delete(_currentOutput); }
            catch (Exception deleteEx) { ErrorLogService.WriteException("Remove failed startup output", deleteEx, _currentOutput); }
            ShowError($"Could not start FFmpeg: {ex.Message}. Check the Error folder, device names, and encoder support.");
        }
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e) => await TogglePauseAsync();

    private async Task TogglePauseAsync()
    {
        if (_ffmpeg is not { HasExited: false }) return;
        try
        {
            int status = _isPaused ? NtResumeProcess(_ffmpeg.Handle) : NtSuspendProcess(_ffmpeg.Handle);
            if (status != 0) { MessageText.Text = $"Windows process pause/resume returned status 0x{status:X8}."; return; }
            if (_isPaused)
            {
                if (_desktopAudioRecorder is not null) await _desktopAudioRecorder.ResumeAsync();
                _recordingStarted = DateTime.Now - _pausedElapsed; _isPaused = false; _timer.Start();
                StatusText.Text = "RECORDING"; StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed; PauseButton.Content = "Ⅱ  Pause"; MessageText.Text = "Recording resumed.";
            }
            else
            {
                _pausedElapsed = DateTime.Now - _recordingStarted; _isPaused = true; _timer.Stop();
                if (_desktopAudioRecorder is not null) await _desktopAudioRecorder.PauseAsync();
                StatusText.Text = "PAUSED"; StatusText.Foreground = System.Windows.Media.Brushes.Gold; PauseButton.Content = "▶  Resume"; MessageText.Text = "Recording process paused. Stop to finalize the file.";
            }
        }
        catch (Exception ex) { ErrorLogService.WriteException("Pause/resume", ex); MessageText.Text = $"Pause/resume failed: {ex.Message}"; }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e) => await StopRecordingAsync();

    private async Task StopRecordingAsync()
    {
        if (_ffmpeg is null || _isStopping) return;
        _isStopping = true;
        var process = _ffmpeg; _timer.Stop();
        bool finalizedCleanly = false;
        try
        {
            if (_isPaused) { NtResumeProcess(process.Handle); _isPaused = false; }
            if (_desktopAudioRecorder is not null)
            {
                try { await _desktopAudioRecorder.StopAsync(); }
                catch (Exception ex) { ErrorLogService.WriteException("Stop WASAPI desktop audio", ex); _stopWarning = $"Desktop audio capture stopped with a warning: {ex.Message}"; }
                finally { _desktopAudioRecorder.Dispose(); _desktopAudioRecorder = null; }
            }
            if (_isWgcRecording)
            {
                try { await _wgc.StopAsync(); } catch (Exception ex) { ErrorLogService.WriteException("Stop native capture", ex); _stopWarning = ex.Message; }
            }
            if (!process.HasExited)
            {
                if (!_isWgcRecording) await process.StandardInput.WriteLineAsync("q");
                try { process.StandardInput.Close(); } catch { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); _stopWarning = "FFmpeg required a forced stop; the recovery marker was retained."; }
            }
            if (process.HasExited && process.ExitCode == 0 && _desktopAudioWav is not null && File.Exists(_desktopAudioWav))
            {
                bool muxed = await MuxDesktopAudioAsync(_currentOutput!, _desktopAudioWav, _recordingIncludesMicrophone);
                if (muxed)
                {
                    try { File.Delete(_desktopAudioWav); } catch (Exception ex) { ErrorLogService.WriteException("Delete temporary desktop audio", ex, _desktopAudioWav); }
                }
                else
                {
                    _stopWarning = "The video was saved, but desktop audio could not be added. The original video was kept; see the Error folder for FFmpeg details.";
                }
                // This WAV is an internal temporary track, not a separate user recording.
                try { if (File.Exists(_desktopAudioWav)) File.Delete(_desktopAudioWav); }
                catch (Exception ex) { ErrorLogService.WriteException("Clean up temporary desktop audio", ex, _desktopAudioWav); }
            }
            finalizedCleanly = process.HasExited && process.ExitCode == 0 &&
                !string.IsNullOrWhiteSpace(_currentOutput) && File.Exists(_currentOutput) &&
                await ValidateRecordingAsync(_currentOutput);
            _desktopAudioWav = null;
            if (!finalizedCleanly && string.IsNullOrWhiteSpace(_stopWarning))
                _stopWarning = "FFmpeg did not produce a verified playable recording. The recovery marker was retained if available.";
        }
        catch (Exception ex)
        {
            ErrorLogService.WriteException("Stop recording", ex, $"Output={_currentOutput}");
            MessageText.Text = $"Stopped with a warning: {ex.Message}";
            try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } } catch { }
        }
        finally
        {
            process.Dispose(); _ffmpeg = null; _recordingReady = false; _isWgcRecording = false;
            if (finalizedCleanly && _recoveryMarker is not null && File.Exists(_recoveryMarker)) { try { File.Delete(_recoveryMarker); } catch (Exception ex) { ErrorLogService.WriteException("Delete recovery marker", ex); } }
            else if (_recoveryMarker is not null && File.Exists(_recoveryMarker)) _stopWarning ??= "The recording may be incomplete; a recovery marker was kept for the next launch.";
            _recoveryMarker = null; StatusText.Text = "READY"; StatusText.Foreground = System.Windows.Media.Brushes.LightSkyBlue; ElapsedText.Text = "00:00:00";
            StartButton.IsEnabled = true; PauseButton.IsEnabled = false; StopButton.IsEnabled = false; ScreenshotButton.IsEnabled = true; ChooseFolderButton.IsEnabled = true; SetConfigurationEnabled(true);
            MessageText.Text = finalizedCleanly
                ? $"Recording finalized and playback-verified: {_currentOutput}" + (_stopWarning is null ? string.Empty : Environment.NewLine + _stopWarning)
                : $"Recording NOT verified; the file may be incomplete or unplayable: {_currentOutput}" + (_stopWarning is null ? string.Empty : Environment.NewLine + _stopWarning);
            _isStopping = false;
            RefreshRecordingsLibrary();
            if (AutoOpenCheckBox.IsChecked == true) OpenFolderButton_Click(this, new RoutedEventArgs());
        }
    }

    private async Task<bool> MuxDesktopAudioAsync(string videoPath, string audioPath, bool mixMicrophone)
    {
        var extension = Path.GetExtension(videoPath);
        var muxedPath = Path.Combine(Path.GetDirectoryName(videoPath)!,
            Path.GetFileNameWithoutExtension(videoPath) + "_audio-mux" + extension);
        try
        {
            if (File.Exists(muxedPath)) File.Delete(muxedPath);

            // The microphone is recorded directly into the video by FFmpeg. Only mix it
            // with loopback audio when the finalized video really contains an audio stream.
            // A checked microphone box alone is not proof that DirectShow opened it.
            bool videoHasMicrophoneAudio = mixMicrophone && await HasAudioStreamAsync(videoPath);
            var args = new List<string> { "-hide_banner", "-y", "-i", videoPath, "-i", audioPath };
            if (videoHasMicrophoneAudio)
            {
                args.AddRange(new[] { "-filter_complex", "[0:a:0][1:a:0]amix=inputs=2:duration=longest:dropout_transition=2[aout]",
                    "-map", "0:v:0", "-map", "[aout]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k" });
            }
            else
            {
                // If microphone capture did not start, include desktop audio by itself
                // instead of referencing a nonexistent [0:a:0] stream and failing the mux.
                args.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k" });
            }
            args.Add("-shortest");
            if (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(muxedPath);

            using var muxer = Process.Start(CreateProcessStartInfo(args, false, true, true))
                ?? throw new InvalidOperationException("Could not start FFmpeg to add desktop audio.");
            var stdoutTask = muxer.StandardOutput.ReadToEndAsync();
            var stderrTask = muxer.StandardError.ReadToEndAsync();
            await muxer.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (muxer.ExitCode != 0 || !File.Exists(muxedPath) || new FileInfo(muxedPath).Length < 4096)
            {
                ErrorLogService.Write("ERROR", $"Could not mux WASAPI desktop audio. Exit code={muxer.ExitCode}; microphone audio detected={videoHasMicrophoneAudio}; video={videoPath}; audio={audioPath}; FFmpeg output={stderr}{Environment.NewLine}{stdout}");
                try { if (File.Exists(muxedPath)) File.Delete(muxedPath); } catch { }
                return false;
            }

            File.Replace(muxedPath, videoPath, null, true);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogService.WriteException("Mux WASAPI desktop audio", ex, $"Video={videoPath}; audio={audioPath}");
            try { if (File.Exists(muxedPath)) File.Delete(muxedPath); } catch { }
            return false;
        }
    }

    private async Task<bool> HasAudioStreamAsync(string videoPath)
    {
        try
        {
            // FFmpeg prints stream metadata to stderr even when it exits with an error
            // because no output was supplied. Inspect the metadata rather than assuming
            // that a selected microphone was successfully recorded.
            var args = new[] { "-hide_banner", "-i", videoPath };
            using var probe = Process.Start(CreateProcessStartInfo(args, false, true, true))
                ?? throw new InvalidOperationException("Could not inspect the finalized recording's audio streams.");
            var stdoutTask = probe.StandardOutput.ReadToEndAsync();
            var stderrTask = probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync();
            var output = (await stderrTask) + Environment.NewLine + (await stdoutTask);
            return System.Text.RegularExpressions.Regex.IsMatch(output,
                @"Stream #\d+:\d+(?:\([^)]*\))?: Audio:",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch (Exception ex)
        {
            ErrorLogService.WriteException("Detect microphone audio stream before mux", ex, videoPath);
            return false;
        }
    }

    private async Task<bool> ValidateRecordingAsync(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 4096)
            {
                ErrorLogService.Write("ERROR", $"Recording validation failed: output is missing or unexpectedly small. File={path}");
                return false;
            }

            var args = new[] { "-hide_banner", "-v", "error", "-xerror", "-i", path,
                "-map", "0:v:0", "-map", "0:a?", "-f", "null", "NUL" };
            using var validator = Process.Start(CreateProcessStartInfo(args, false, true, true))
                ?? throw new InvalidOperationException("Could not start FFmpeg playback validation.");
            var stdoutTask = validator.StandardOutput.ReadToEndAsync();
            var stderrTask = validator.StandardError.ReadToEndAsync();
            await validator.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (validator.ExitCode == 0) return true;

            ErrorLogService.Write("ERROR", $"Recording failed full decode validation. Exit code={validator.ExitCode}; file={path}; FFmpeg output={stderr}{Environment.NewLine}{stdout}");
            return false;
        }
        catch (Exception ex)
        {
            ErrorLogService.WriteException("Validate finalized recording", ex, path);
            return false;
        }
    }

    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_outputFolder, $"Nova_Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png");
        if (CaptureBackendCombo.SelectedIndex == 0)
        {
            try
            {
                if (_wgc.SelectedItem is null)
                {
                    var picked = await _wgc.PickItemAsync(new WindowInteropHelper(this).Handle);
                    if (!picked) return;
                    SelectedCaptureText.Text = $"Native capture target selected • {_wgc.SelectedItem!.Size.Width} × {_wgc.SelectedItem.Size.Height}";
                }
                await _wgc.SaveScreenshotAsync(path, CursorCheckBox.IsChecked == true);
                MessageText.Text = $"Native Windows Graphics Capture screenshot saved: {path}";
                RefreshRecordingsLibrary();
            }
            catch (Exception ex) { ErrorLogService.WriteException("Native screenshot", ex, path); ShowError($"Native screenshot failed: {ex.Message}"); }
            return;
        }
        if (!EnsureFfmpeg()) return;
        var args = new List<string> { "-hide_banner", "-y", "-f", "gdigrab", "-framerate", "1", "-draw_mouse", CursorCheckBox.IsChecked == true ? "1" : "0" };
        if (CaptureModeCombo.SelectedIndex == 2)
        {
            if (string.IsNullOrWhiteSpace(WindowTitleBox.Text)) { ShowError("Enter a window title first."); return; }
            args.AddRange(new[] { "-i", $"title={WindowTitleBox.Text.Trim()}" });
        }
        else if (CaptureModeCombo.SelectedIndex == 3)
        {
            if (!int.TryParse(RegionXBox.Text, out int x) || !int.TryParse(RegionYBox.Text, out int y) || !int.TryParse(RegionWidthBox.Text, out int w) || !int.TryParse(RegionHeightBox.Text, out int h) || w < 16 || h < 16) { ShowError("Enter valid region values first."); return; }
            args.AddRange(new[] { "-offset_x", x.ToString(), "-offset_y", y.ToString(), "-video_size", $"{w}x{h}", "-i", "desktop" });
        }
        else if (CaptureModeCombo.SelectedIndex == 1 && MonitorCombo.SelectedItem is MonitorChoice monitor)
            args.AddRange(new[] { "-offset_x", monitor.Bounds.X.ToString(), "-offset_y", monitor.Bounds.Y.ToString(), "-video_size", $"{monitor.Bounds.Width}x{monitor.Bounds.Height}", "-i", "desktop" });
        else args.AddRange(new[] { "-i", "desktop" });
        args.AddRange(new[] { "-frames:v", "1", path });
        try
        {
            var p = Process.Start(CreateProcessStartInfo(args, redirectInput: false, redirectOutput: false, redirectError: true));
            if (p is null) throw new InvalidOperationException("Could not start FFmpeg.");
            string error = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
            if (p.ExitCode == 0 && File.Exists(path)) { MessageText.Text = $"Screenshot saved: {path}"; RefreshRecordingsLibrary(); }
            else ShowError($"Screenshot failed. {error}");
        }
        catch (Exception ex) { ErrorLogService.WriteException("Screenshot", ex, $"Output={path}"); ShowError($"Screenshot failed: {ex.Message}"); }
    }

    private async void CheckFfmpegButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureFfmpeg()) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo { FileName = FfmpegPath, Arguments = "-version", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p is null) throw new InvalidOperationException("FFmpeg could not be launched.");
            string version = await p.StandardOutput.ReadLineAsync() ?? await p.StandardError.ReadLineAsync() ?? "FFmpeg responded."; await p.WaitForExitAsync();
            MessageText.Text = p.ExitCode == 0 ? version : $"FFmpeg check returned exit code {p.ExitCode}.";
        }
        catch (Exception ex) { ErrorLogService.WriteException("FFmpeg check", ex, $"FFmpeg={FfmpegPath}"); ShowError($"FFmpeg check failed: {ex.Message}"); }
    }

    private async void ListDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadDirectShowDevicesAsync(showMessage: true);
    }

    private async Task LoadDirectShowDevicesAsync(bool showMessage)
    {
        if (!EnsureFfmpeg()) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = FfmpegPath,
                Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });
            if (p is null) throw new InvalidOperationException("FFmpeg could not be launched.");
            string output = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Directory.CreateDirectory(_outputFolder);
            File.WriteAllText(Path.Combine(_outputFolder, "Nova-Audio-Devices.txt"), output);

            var audioDevices = System.Text.RegularExpressions.Regex.Matches(
                output, "\\\"([^\\\"\\r\\n]+)\\\"\\s*\\(audio\\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var videoDevices = System.Text.RegularExpressions.Regex.Matches(
                output, "\\\"([^\\\"\\r\\n]+)\\\"\\s*\\(video\\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            PopulateAudioDeviceCombo(MicDeviceBox, audioDevices, MicDeviceBox.Text);
            PopulateAudioDeviceCombo(DesktopAudioDeviceBox, audioDevices, DesktopAudioDeviceBox.Text);
            PopulateAudioDeviceCombo(WebcamDeviceBox, videoDevices, WebcamDeviceBox.Text);
            SaveSettings();

            if (showMessage)
                MessageText.Text = $"Found {audioDevices.Count} DirectShow audio device(s) and {videoDevices.Count} webcam/video device(s). Device selectors updated.";
        }
        catch (Exception ex)
        {
            ErrorLogService.WriteException("Load DirectShow devices", ex);
            if (showMessage)
                ShowError($"Could not list devices: {ex.Message}");
            else
                MessageText.Text = $"Devices could not load automatically: {ex.Message}. You can retry with List DirectShow devices.";
        }
    }

    private static string BuildDirectShowInput(string mediaType, string deviceName)
    {
        // ArgumentList keeps this entire value in one argument. Do not embed
        // shell-style quotes: FFmpeg treats them as literal device-name characters.
        string name = deviceName.Trim().Trim('"');
        return mediaType + "=" + name;
    }

    private static void PopulateAudioDeviceCombo(System.Windows.Controls.ComboBox combo, IReadOnlyCollection<string> devices, string currentText)
    {
        string previousSelection = currentText.Trim().Trim('"');
        combo.Items.Clear();
        foreach (var device in devices) combo.Items.Add(device);

        if (!string.IsNullOrWhiteSpace(previousSelection))
        {
            var matching = devices.FirstOrDefault(name => name.Equals(previousSelection, StringComparison.OrdinalIgnoreCase));
            combo.Text = matching ?? previousSelection;
        }
        else if (devices.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void MicDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) SaveSettings();
    }

    private void DesktopAudioDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) SaveSettings();
    }

    private void WebcamDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) SaveSettings();
    }

    private void WebcamDeviceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SaveSettings();
    }

    private void DesktopAudioDeviceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SaveSettings();
    }

    private async void HandleUnexpectedExit()
    {
        if (_ffmpeg is null || !_ffmpeg.HasExited || _isStopping || !_recordingReady) return;
        var failedEncoder = _activeEncoder;
        var failedProcess = _ffmpeg;
        _timer.Stop(); StatusText.Text = "STOPPED"; StatusText.Foreground = System.Windows.Media.Brushes.Gold;
        StartButton.IsEnabled = true; PauseButton.IsEnabled = false; StopButton.IsEnabled = false; ScreenshotButton.IsEnabled = true; ChooseFolderButton.IsEnabled = true; SetConfigurationEnabled(true);
        ErrorLogService.Write("FFMPEG", $"FFmpeg exited unexpectedly. Exit code={failedProcess.ExitCode}; encoder={failedEncoder}; output={_currentOutput ?? "(not set)"}");
        if (_isWgcRecording) { try { await _wgc.StopAsync(); } catch (Exception ex) { ErrorLogService.WriteException("Stop capture after encoder failure", ex); } }
        _isWgcRecording = false;
        MessageText.Text = "FFmpeg exited. Review the Error folder and inspect the output file.";
        try { failedProcess.Dispose(); } catch { } _ffmpeg = null; _recordingReady = false;
        RefreshRecordingsLibrary();
        if (!_lastFfmpegInputFailure && _automaticEncoder && failedEncoder != "mpeg4" && _failedEncoders.Add(failedEncoder))
        {
            MessageText.Text = $"Encoder {failedEncoder} stopped unexpectedly. Retrying with the next available encoder.";
            await StartRecordingAsync(true, true);
        }
    }

    private bool EnsureFfmpeg()
    {
        if (FfmpegPath.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || File.Exists(FfmpegPath)) return true;
        ErrorLogService.Write("ERROR", $"FFmpeg was not found. Configured path={FfmpegPath}");
        ShowError("FFmpeg was not found at the configured path. Enter a valid ffmpeg.exe path or add FFmpeg to PATH."); return false;
    }

    private void SetConfigurationEnabled(bool enabled)
    {
        CaptureModeCombo.IsEnabled = CaptureBackendCombo.IsEnabled = MonitorCombo.IsEnabled = WindowTitleBox.IsEnabled = RegionXBox.IsEnabled = RegionYBox.IsEnabled = RegionWidthBox.IsEnabled = RegionHeightBox.IsEnabled = enabled;
        QualityCombo.IsEnabled = EncoderCombo.IsEnabled = FpsCombo.IsEnabled = FormatCombo.IsEnabled = FfmpegPathBox.IsEnabled = MicCheckBox.IsEnabled = MicDeviceBox.IsEnabled = DesktopAudioCheckBox.IsEnabled = WebcamCheckBox.IsEnabled = WebcamDeviceBox.IsEnabled = WebcamWidthBox.IsEnabled = WebcamPositionCombo.IsEnabled = CursorCheckBox.IsEnabled = enabled;
        ScheduleCheckBox.IsEnabled = ScheduleDatePicker.IsEnabled = ScheduleTimeBox.IsEnabled = CountdownSecondsBox.IsEnabled = enabled;
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != (ModifierKeys.Control | ModifierKeys.Shift))) return;
        if (e.Key == Key.R) { if (_ffmpeg is { HasExited: false }) _ = StopRecordingAsync(); else _ = StartRecordingAsync(); e.Handled = true; }
        else if (e.Key == Key.P) { _ = TogglePauseAsync(); e.Handled = true; }
        else if (e.Key == Key.S) { ScreenshotButton_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void RegisterGlobalHotkeys()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            _windowSource = HwndSource.FromHwnd(handle);
            _windowSource?.AddHook(GlobalHotkeyHook);
            bool start = RegisterHotKey(handle, HotkeyStartStop, ModControl | ModShift | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(Key.R));
            bool pause = RegisterHotKey(handle, HotkeyPause, ModControl | ModShift | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(Key.P));
            bool shot = RegisterHotKey(handle, HotkeyScreenshot, ModControl | ModShift | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(Key.S));
            if (!start || !pause || !shot)
                ErrorLogService.Write("WARN", "One or more global hotkeys could not be registered; another app may already use the shortcut. In-app shortcuts remain available.");
        }
        catch (Exception ex) { ErrorLogService.WriteException("Register global hotkeys", ex); }
    }

    private void UnregisterGlobalHotkeys()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HotkeyStartStop); UnregisterHotKey(handle, HotkeyPause); UnregisterHotKey(handle, HotkeyScreenshot);
            _windowSource?.RemoveHook(GlobalHotkeyHook); _windowSource = null;
        }
        catch (Exception ex) { ErrorLogService.WriteException("Unregister global hotkeys", ex); }
    }

    private IntPtr GlobalHotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case HotkeyStartStop:
                if (_ffmpeg is { HasExited: false }) _ = StopRecordingAsync(); else _ = StartRecordingAsync();
                break;
            case HotkeyPause: _ = TogglePauseAsync(); break;
            case HotkeyScreenshot:
                if (ScreenshotButton.IsEnabled) ScreenshotButton_Click(this, new RoutedEventArgs());
                else MessageText.Text = "Stop the current recording before taking a screenshot in this build.";
                break;
        }
        return IntPtr.Zero;
    }

    private ProcessStartInfo CreateProcessStartInfo(IEnumerable<string> arguments, bool redirectInput, bool redirectOutput, bool redirectError)
    {
        var executable = FfmpegPath;
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectError,
            WorkingDirectory = Directory.Exists(Path.GetDirectoryName(executable)) ? Path.GetDirectoryName(executable)! : Environment.CurrentDirectory
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }


    private async void ChooseWgcItemButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picked = await _wgc.PickItemAsync(new WindowInteropHelper(this).Handle);
            SelectedCaptureText.Text = picked && _wgc.SelectedItem is not null
                ? $"Native capture target selected • {_wgc.SelectedItem.Size.Width} × {_wgc.SelectedItem.Size.Height}"
                : "Native capture target: selection cancelled";
            if (picked) CaptureBackendCombo.SelectedIndex = 0;
        }
        catch (Exception ex) { ErrorLogService.WriteException("Choose native capture target", ex); ShowError($"Windows Graphics Capture picker failed: {ex.Message}"); }
    }

    private void RefreshMonitorsButton_Click(object sender, RoutedEventArgs e) => RefreshMonitors();

    private void RefreshMonitors()
    {
        if (MonitorCombo is null) return;
        var previous = (MonitorCombo.SelectedItem as MonitorChoice)?.DeviceName;
        MonitorCombo.Items.Clear();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            MonitorCombo.Items.Add(new MonitorChoice(screen.DeviceName, screen.Bounds, screen.Primary));
        MonitorCombo.SelectedItem = MonitorCombo.Items.Cast<MonitorChoice>().FirstOrDefault(m => m.DeviceName == previous)
            ?? MonitorCombo.Items.Cast<MonitorChoice>().FirstOrDefault(m => m.Primary)
            ?? MonitorCombo.Items.Cast<MonitorChoice>().FirstOrDefault();
    }

    private void PickRegionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Hide();
            using var picker = new RegionPickerForm();
            if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK && picker.SelectedBounds is Rectangle r)
            {
                RegionXBox.Text = r.X.ToString(); RegionYBox.Text = r.Y.ToString(); RegionWidthBox.Text = r.Width.ToString(); RegionHeightBox.Text = r.Height.ToString();
                CaptureModeCombo.SelectedIndex = 3; CaptureBackendCombo.SelectedIndex = 1;
                MessageText.Text = $"Region selected in virtual-desktop physical pixels: {r.X}, {r.Y}, {r.Width} × {r.Height}.";
            }
        }
        catch (Exception ex) { ErrorLogService.WriteException("Visual region picker", ex); ShowError($"Region picker failed: {ex.Message}"); }
        finally { Show(); Activate(); }
    }

    private async void DetectEncodersButton_Click(object sender, RoutedEventArgs e)
    {
        await DetectEncodersAsync();
        MessageText.Text = "Available encoders: " + (new[] { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libx264", "mpeg4" }.Where(_availableEncoders.Contains).DefaultIfEmpty("none detected; MPEG-4 fallback will be attempted").Aggregate((a, b) => a + ", " + b));
    }

    private async Task DetectEncodersAsync()
    {
        if (_availableEncoders.Count > 0 || !EnsureFfmpeg()) return;
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo { FileName = FfmpegPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "-hide_banner", "-encoders" } } };
            process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); var text = await stdout + Environment.NewLine + await stderr;
            foreach (var encoder in new[] { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libx264", "mpeg4" })
                if (text.Contains(encoder, StringComparison.OrdinalIgnoreCase)) _availableEncoders.Add(encoder);
            // FFmpeg's built-in MPEG-4 Part 2 encoder is the last-resort fallback in LGPL builds that omit libx264.
        }
        catch (Exception ex) { ErrorLogService.WriteException("Detect FFmpeg encoders", ex); _availableEncoders.Add("mpeg4"); }
    }

    private string SelectEncoder()
    {
        _automaticEncoder = EncoderCombo.SelectedIndex == 0;
        return RecordingUtilities.SelectEncoder(EncoderCombo.SelectedIndex, _availableEncoders, false, _failedEncoders);
    }

    private void CancelScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        _scheduleTimer.Stop(); _scheduledStart = null; _countdownCancellation?.Cancel(); ScheduleCheckBox.IsChecked = false; StartButton.IsEnabled = _ffmpeg is not { HasExited: false }; MessageText.Text = "Scheduled recording/countdown cancelled.";
    }

    private void RecoverPendingRecordings()
    {
        try
        {
            foreach (var markerPath in Directory.EnumerateFiles(_outputFolder, "*.nova-recovery.json"))
            {
                RecoveryMarker? marker;
                try { marker = JsonSerializer.Deserialize<RecoveryMarker>(File.ReadAllText(markerPath)); }
                catch (Exception ex) { ErrorLogService.WriteException("Read recovery marker", ex, markerPath); continue; }
                if (marker is null || !File.Exists(marker.OutputPath)) { try { File.Delete(markerPath); } catch { } continue; }
                bool ffmpegStillRunning = false;
                try { using var proc = Process.GetProcessById(marker.ProcessId); ffmpegStillRunning = !proc.HasExited && proc.ProcessName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase); } catch { }
                if (ffmpegStillRunning)
                {
                    System.Windows.MessageBox.Show(this, $"An earlier FFmpeg process (PID {marker.ProcessId}) may still be recording:\n{marker.OutputPath}\n\nStop that process before attempting recovery.", "Nova recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }
                if (System.Windows.MessageBox.Show(this, $"Nova found a recording that may not have been finalized:\n\n{marker.OutputPath}\n\nAttempt to recover readable frames into a new MKV file? The original will be preserved.", "Recover interrupted recording", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) continue;
                _ = RecoverOneAsync(markerPath, marker);
            }
        }
        catch (Exception ex) { ErrorLogService.WriteException("Scan interrupted recordings", ex); }
    }

    private async Task RecoverOneAsync(string markerPath, RecoveryMarker marker)
    {
        try
        {
            var output = Path.Combine(Path.GetDirectoryName(marker.OutputPath)!, Path.GetFileNameWithoutExtension(marker.OutputPath) + "_recovered.mkv");
            var args = new[] { "-hide_banner", "-y", "-i", marker.OutputPath, "-map", "0", "-c", "copy", output };
            using var p = Process.Start(CreateProcessStartInfo(args, false, true, true)) ?? throw new InvalidOperationException("FFmpeg could not be started.");
            var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
            if (p.ExitCode == 0 && File.Exists(output)) { File.Delete(markerPath); MessageText.Text = $"Recovered recording saved: {output}"; RefreshRecordingsLibrary(); }
            else ShowError("Recovery could not finalize the recording. The original file and recovery marker were preserved. " + await stderr);
        }
        catch (Exception ex) { ErrorLogService.WriteException("Recover interrupted recording", ex, marker.OutputPath); ShowError($"Recovery failed: {ex.Message}"); }
    }

    private async void TrimSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (RecordingsListBox.SelectedItem is not ListBoxItem { Tag: string input } || !File.Exists(input) || !new[] { ".mp4", ".mkv", ".mov", ".avi", ".webm" }.Contains(Path.GetExtension(input), StringComparer.OrdinalIgnoreCase))
        { MessageText.Text = "Select a video recording to trim."; return; }
        if (!RecordingUtilities.TryParseTrimRange(TrimStartBox.Text, TrimEndBox.Text, out var start, out var duration))
        { ShowError("Trim start and end must be valid times, and end must be later than start."); return; }
        var output = Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + "_trimmed" + Path.GetExtension(input));
        if (File.Exists(output)) output = Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + $"_trimmed_{DateTime.Now:HHmmss}" + Path.GetExtension(input));
        try
        {
            var args = new[] { "-hide_banner", "-y", "-ss", start.ToString(@"hh\:mm\:ss\.fff"), "-i", input, "-t", duration.ToString(@"hh\:mm\:ss\.fff"), "-map", "0", "-c", "copy", output };
            using var p = Process.Start(CreateProcessStartInfo(args, false, true, true)) ?? throw new InvalidOperationException("FFmpeg could not be started.");
            var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
            if (p.ExitCode == 0 && File.Exists(output)) { MessageText.Text = $"Trimmed file saved: {output}"; RefreshRecordingsLibrary(); }
            else ShowError("Trim failed: " + await stderr);
        }
        catch (Exception ex) { ErrorLogService.WriteException("Trim recording", ex, input); ShowError($"Trim failed: {ex.Message}"); }
    }

    private void RefreshRecordingsButton_Click(object sender, RoutedEventArgs e) => RefreshRecordingsLibrary();

    private void RefreshRecordingsLibrary()
    {
        if (RecordingsListBox is null) return;
        try
        {
            Directory.CreateDirectory(_outputFolder);
            RecordingsListBox.Items.Clear();
            foreach (var file in Directory.EnumerateFiles(_outputFolder)
                         .Where(f => new[] { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".png", ".jpg" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                         .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(100))
            {
                var item = new ListBoxItem { Content = $"{file.Name}   •   {file.Length / (1024d * 1024):F1} MB   •   {file.LastWriteTime:yyyy-MM-dd HH:mm}", Tag = file.FullName, ToolTip = file.FullName };
                RecordingsListBox.Items.Add(item);
            }
        }
        catch (Exception ex) { ErrorLogService.WriteException("Refresh recordings library", ex); MessageText.Text = $"Could not refresh recordings: {ex.Message}"; }
    }

    private void OpenSelectedRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (RecordingsListBox.SelectedItem is not ListBoxItem { Tag: string path } || !File.Exists(path))
        { MessageText.Text = "Select an existing recording or screenshot first."; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ErrorLogService.WriteException("Open selected recording", ex, path); ShowError($"Could not open the selected file: {ex.Message}"); }
    }

    private void DeleteSelectedRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (RecordingsListBox.SelectedItem is not ListBoxItem { Tag: string path } || !File.Exists(path))
        { MessageText.Text = "Select an existing recording or screenshot first."; return; }
        if (string.Equals(path, _currentOutput, StringComparison.OrdinalIgnoreCase) && _ffmpeg is { HasExited: false })
        { ShowError("You cannot delete the file that is currently being recorded."); return; }
        if (System.Windows.MessageBox.Show(this, $"Permanently delete this file?\n\n{Path.GetFileName(path)}", "Delete recording", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { File.Delete(path); RefreshRecordingsLibrary(); MessageText.Text = "File deleted."; }
        catch (Exception ex) { ErrorLogService.WriteException("Delete recording", ex, path); ShowError($"Could not delete the file: {ex.Message}"); }
    }

    private void UpdateElapsed()
    {
        var elapsed = _isPaused ? _pausedElapsed : DateTime.Now - _recordingStarted;
        ElapsedText.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        if (!_isPaused && _ffmpeg is { HasExited: false } && DateTime.Now - _lastDiskCheck >= TimeSpan.FromSeconds(10))
        {
            _lastDiskCheck = DateTime.Now;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(_outputFolder));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && drive.AvailableFreeSpace < EmergencyFreeBytes)
                    {
                        ErrorLogService.Write("ERROR", $"Recording auto-stopped because free disk space fell below 256 MB. Available={drive.AvailableFreeSpace}; output={_currentOutput}");
                        _stopWarning = "The recording was stopped to protect the disk because less than 256 MB remained free.";
                        MessageText.Text = _stopWarning;
                        _ = StopRecordingAsync();
                    }
                }
            }
            catch (Exception ex) { ErrorLogService.WriteException("Monitor free disk space", ex); }
        }
    }
    private int SelectedFps() => (FpsCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch { "30 FPS" => 30, "120 FPS" => 120, _ => 60 };
    private string SelectedPreset() => QualityCombo.SelectedIndex switch { 0 => "veryfast", 2 => "slow", 3 => "veryslow", _ => "medium" };
    private int SelectedCrf() => QualityCombo.SelectedIndex switch { 0 => 28, 2 => 18, 3 => 14, _ => 23 };
    private string SelectedBitrate() => QualityCombo.SelectedIndex switch { 0 => "4M", 2 => "10M", 3 => "16M", _ => "6M" };
    private int SelectedMpeg4Quality() => QualityCombo.SelectedIndex switch { 0 => 7, 2 => 3, 3 => 2, _ => 5 };
    private static string QuoteArgument(string argument) => "\"" + argument.Replace("\"", "\\\"") + "\"";
    private void DiagnosticsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _diagnosticsEnabled = DiagnosticsCheckBox.IsChecked == true;
        ErrorLogService.Enabled = _diagnosticsEnabled;
        if (_diagnosticsEnabled)
            ErrorLogService.Write("INFO", "Diagnostics re-enabled by the user.");
        SaveSettings();
        MessageText.Text = _diagnosticsEnabled
            ? $"Error logging is enabled. Logs are kept in: {ErrorLogService.FolderPath}"
            : "Error logging is disabled. Existing log files were not deleted.";
    }

    private void OpenErrorFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ErrorLogService.FolderPath);
            Process.Start(new ProcessStartInfo("explorer.exe", ErrorLogService.FolderPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLogService.WriteException("Open Error folder", ex);
            System.Windows.MessageBox.Show(this, $"Could not open Error folder: {ex.Message}", "Nova Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowError(string message)
    {
        ErrorLogService.Write("ERROR", message);
        MessageText.Text = message;
        System.Windows.MessageBox.Show(this, message, "Nova Screen Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private sealed record MonitorChoice(string DeviceName, Rectangle Bounds, bool Primary)
    {
        public string DisplayName => $"{(Primary ? "Primary" : "Monitor")} • {DeviceName} • {Bounds.Width}×{Bounds.Height} at ({Bounds.X},{Bounds.Y})";
    }
    private sealed record RecoveryMarker(string OutputPath, int ProcessId, DateTime StartedUtc, string Encoder);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
