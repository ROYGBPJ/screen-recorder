using NAudio.Wave;
using System.IO;

namespace NovaScreenRecorder.App;

/// <summary>
/// Captures the Windows default playback mix through WASAPI loopback.
/// Audio is written to a temporary WAV and muxed into the finalized recording afterward,
/// so an unavailable audio device cannot cause FFmpeg to abandon the video container.
/// </summary>
internal sealed class WasapiLoopbackRecorder : IDisposable
{
    private readonly string _outputPath;
    private readonly object _writerLock = new();
    private WasapiLoopbackCapture? _capture;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<bool>? _stopped;
    private Exception? _captureError;
    private bool _started;
    private bool _disposed;

    public WasapiLoopbackRecorder(string outputPath) => _outputPath = outputPath;

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        _capture = new WasapiLoopbackCapture();
        _writer = new WaveFileWriter(_outputPath, _capture.WaveFormat);
        _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.DataAvailable += Capture_DataAvailable;
        _capture.RecordingStopped += Capture_RecordingStopped;
        try
        {
            _capture.StartRecording();
            _started = true;
            return Task.CompletedTask;
        }
        catch
        {
            _capture.DataAvailable -= Capture_DataAvailable;
            _capture.RecordingStopped -= Capture_RecordingStopped;
            _writer.Dispose();
            _writer = null;
            _capture.Dispose();
            _capture = null;
            throw;
        }
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            lock (_writerLock)
            {
                if (_writer is not null && e.BytesRecorded > 0)
                    _writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
        }
        catch (Exception ex)
        {
            _captureError ??= ex;
        }
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _captureError ??= e.Exception;
        _stopped?.TrySetResult(true);
    }

    public async Task PauseAsync()
    {
        if (_capture is null || !_started) return;
        _capture.StopRecording();
        if (_stopped is not null)
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(8));
        _started = false;
        _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task ResumeAsync()
    {
        if (_capture is null || _started) return Task.CompletedTask;
        _capture.StartRecording();
        _started = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_capture is null) return;
        if (_started)
        {
            try { _capture.StopRecording(); }
            catch (Exception ex) { _captureError ??= ex; }
            try
            {
                if (_stopped is not null)
                    await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch (TimeoutException ex) { _captureError ??= ex; }
            _started = false;
        }

        _capture.DataAvailable -= Capture_DataAvailable;
        _capture.RecordingStopped -= Capture_RecordingStopped;
        lock (_writerLock)
        {
            try { _writer?.Flush(); } catch (Exception ex) { _captureError ??= ex; }
            try { _writer?.Dispose(); } catch (Exception ex) { _captureError ??= ex; }
            _writer = null;
        }
        _capture.Dispose();
        _capture = null;

        if (_captureError is not null)
            throw new IOException("WASAPI loopback audio capture failed.", _captureError);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _capture = null;
        lock (_writerLock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
