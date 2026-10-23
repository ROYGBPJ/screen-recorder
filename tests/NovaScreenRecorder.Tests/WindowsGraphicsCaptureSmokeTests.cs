using Windows.Graphics.Capture;
using Xunit;

namespace NovaScreenRecorder.Tests;

public sealed class WindowsGraphicsCaptureSmokeTests
{
    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void WindowsGraphicsCaptureSupportProbeRunsOnWindows()
    {
        // CI can verify the API surface exists; a true result and actual capture require a supported desktop session.
        var supported = GraphicsCaptureSession.IsSupported();
        Assert.IsType<bool>(supported);
    }
}
