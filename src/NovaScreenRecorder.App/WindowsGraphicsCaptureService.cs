using Microsoft.Graphics.Canvas;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT.Interop;

namespace NovaScreenRecorder.App;

/// <summary>
/// Native Windows.Graphics.Capture frame source. Frames are delivered as BGRA raw video to FFmpeg's stdin;
/// FFmpeg remains responsible for encoding and audio muxing. This API requires a supported Windows build.
/// </summary>
internal sealed class WindowsGraphicsCaptureService : IDisposable
{
    private CanvasDevice? _device;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private Stream? _output;
    private readonly object _writeLock = new();
    private readonly object _frameLock = new();
    private readonly Stopwatch _clock = new();
    private byte[]? _latestPixels;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pumpTask;
    private long _lastFrameTicks;
    private int _fps = 60;
    private SizeInt32 _targetSize;
    private bool _stopping;
    private Exception? _lastError;

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();
    public GraphicsCaptureItem? SelectedItem => _item;
    public event Action<Exception>? CaptureError;

    public async Task<bool> PickItemAsync(IntPtr ownerHwnd)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Windows Graphics Capture is not supported on this Windows build.");
        var picker = new GraphicsCapturePicker();
        InitializeWithWindow.Initialize(picker, ownerHwnd);
        _item = await picker.PickSingleItemAsync();
        return _item is not null;
    }

    public async Task SaveScreenshotAsync(string path, bool captureCursor = true)
    {
        if (_item is null) throw new InvalidOperationException("Choose a display or window using the native picker first.");
        using var device = CanvasDevice.GetSharedDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        using var session = pool.CreateCaptureSession(_item);
        try { session.IsCursorCaptureEnabled = captureCursor; } catch (NotSupportedException) { }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<Direct3D11CaptureFramePool, object>? handler = null;
        handler = async (sender, _) =>
        {
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
                await bitmap.SaveAsync(path, CanvasBitmapFileFormat.Png);
                completion.TrySetResult(true);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        };
        pool.FrameArrived += handler;
        try
        {
            session.StartCapture();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await completion.Task.WaitAsync(timeout.Token);
        }
        finally { pool.FrameArrived -= handler; }
    }

    public void Start(Stream output, int fps, bool captureCursor = true)
    {
        if (_item is null) throw new InvalidOperationException("Choose a display or window using the Windows Graphics Capture picker first.");
        if (_pool is not null) throw new InvalidOperationException("Capture is already running.");
        _output = output;
        _fps = Math.Clamp(fps, 1, 120);
        _lastFrameTicks = 0;
        _stopping = false;
        _lastError = null;
        _device ??= CanvasDevice.GetSharedDevice();
        var size = _item.Size;
        _targetSize = size;
        if (size.Width <= 0 || size.Height <= 0) throw new InvalidOperationException("The selected capture item has an invalid size.");
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = captureCursor; } catch (NotSupportedException) { }
        _latestPixels = null;
        _clock.Restart();
        _session.StartCapture();
        _pumpCancellation = new CancellationTokenSource();
        _pumpTask = PumpFramesAsync(_pumpCancellation.Token);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null || _stopping || _device is null || _output is null) return;
            var now = _clock.ElapsedTicks;
            var minTicks = Stopwatch.Frequency / _fps;
            if (now - Interlocked.Read(ref _lastFrameTicks) < minTicks) return;
            Interlocked.Exchange(ref _lastFrameTicks, now);
            var content = frame.ContentSize;
            if (content.Width <= 0 || content.Height <= 0) return;
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, frame.Surface);
            byte[] pixels;
            if (bitmap.SizeInPixels.Width != _targetSize.Width || bitmap.SizeInPixels.Height != _targetSize.Height)
            {
                using var resized = new CanvasRenderTarget(_device, _targetSize.Width, _targetSize.Height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
                using (var drawing = resized.CreateDrawingSession())
                {
                    drawing.Clear(Windows.UI.Colors.Black);
                    drawing.DrawImage(bitmap, new Windows.Foundation.Rect(0, 0, _targetSize.Width, _targetSize.Height));
                }
                pixels = resized.GetPixelBytes();
            }
            else pixels = bitmap.GetPixelBytes();
            if (pixels.Length != checked(_targetSize.Width * _targetSize.Height * 4)) return;
            lock (_frameLock)
            {
                if (_stopping || _output is null) return;
                _latestPixels = pixels;
            }
        }
        catch (Exception ex)
        {
            _lastError = ex;
            try { CaptureError?.Invoke(ex); } catch { }
        }
    }

    private async Task PumpFramesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / _fps));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                byte[]? pixels;
                lock (_frameLock) pixels = _latestPixels;
                if (pixels is null) continue;
                var output = _output;
                if (_stopping || output is null) break;
                await output.WriteAsync(pixels.AsMemory(), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _lastError = ex;
            try { CaptureError?.Invoke(ex); } catch { }
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        try { _pumpCancellation?.Cancel(); } catch { }
        if (_pumpTask is not null)
        {
            var completed = await Task.WhenAny(_pumpTask, Task.Delay(TimeSpan.FromSeconds(2)));
            if (completed != _pumpTask)
            {
                // If FFmpeg stopped consuming stdin, closing the pipe breaks a blocked write instead of hanging Stop forever.
                lock (_writeLock) { try { _output?.Dispose(); } catch { } _output = null; }
            }
            try { await _pumpTask; } catch (Exception ex) { _lastError ??= ex; }
        }
        _pumpTask = null;
        _pumpCancellation?.Dispose(); _pumpCancellation = null;
        if (_pool is not null) _pool.FrameArrived -= OnFrameArrived;
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        _session = null; _pool = null;
        lock (_writeLock)
        {
            try { _output?.Flush(); } catch (Exception ex) { _lastError ??= ex; }
            try { _output?.Dispose(); } catch (Exception ex) { _lastError ??= ex; }
            _output = null;
        }
        _clock.Stop();
        _latestPixels = null;
        await Task.Yield();
        if (_lastError is not null) throw new IOException("Windows Graphics Capture encountered a frame-stream error.", _lastError);
    }

    public void ClearSelection() => _item = null;

    public void Dispose()
    {
        try { _stopping = true; _pumpCancellation?.Cancel(); _session?.Dispose(); _pool?.Dispose(); _device?.Dispose(); } catch { }
        _pumpCancellation?.Dispose(); _pumpCancellation = null;
        _session = null; _pool = null; _device = null; _output = null; _latestPixels = null;
    }
}
