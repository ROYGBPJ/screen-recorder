using System.Drawing;
using NovaScreenRecorder.App;
using Xunit;

namespace NovaScreenRecorder.Tests;

public sealed class RecordingUtilitiesTests
{
    [Fact]
    public void AutomaticEncoderPrefersAvailableHardwareEncoder()
    {
        Assert.Equal("h264_qsv", RecordingUtilities.SelectEncoder(0, new[] { "libx264", "h264_qsv" }));
    }

    [Fact]
    public void AutomaticEncoderFallsBackToCpuWhenHardwareMissing()
    {
        Assert.Equal("libx264", RecordingUtilities.SelectEncoder(0, new[] { "libx264" }));
    }

    [Theory]
    [InlineData(2, "h264_nvenc")]
    [InlineData(3, "h264_qsv")]
    [InlineData(4, "h264_amf")]
    public void ExplicitEncoderSelectionUsesRequestedEncoderWhenAvailable(int choice, string expected)
    {
        Assert.Equal(expected, RecordingUtilities.SelectEncoder(choice, new[] { expected, "libx264" }));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ExplicitEncoderSelectionFallsBackWhenUnavailable(int choice)
    {
        Assert.Equal("libx264", RecordingUtilities.SelectEncoder(choice, new[] { "libx264" }));
    }

    [Fact]
    public void CpuFallbackPrefersMediaFoundationWhenAvailable()
    {
        Assert.Equal("h264_mf", RecordingUtilities.SelectEncoder(1, new[] { "h264_nvenc", "h264_mf", "mpeg4" }));
    }

    [Fact]
    public void BuiltInMpeg4IsLastResortWhenNoH264EncoderIsAvailable()
    {
        Assert.Equal("mpeg4", RecordingUtilities.SelectEncoder(0, new[] { "mpeg4" }));
    }

    [Fact]
    public void ExcludedEncoderFallsThroughToNextCandidate()
    {
        Assert.Equal("mpeg4", RecordingUtilities.SelectEncoder(0, new[] { "h264_nvenc", "h264_mf", "mpeg4" }, excludedEncoders: new[] { "h264_nvenc", "h264_mf" }));
    }

    [Fact]
    public void TrimRangeReturnsStartAndDuration()
    {
        Assert.True(RecordingUtilities.TryParseTrimRange("00:00:05", "00:00:12", out var start, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(5), start);
        Assert.Equal(TimeSpan.FromSeconds(7), duration);
    }

    [Theory]
    [InlineData("00:00:10", "00:00:10")]
    [InlineData("00:00:12", "00:00:05")]
    [InlineData("bad", "00:00:10")]
    public void InvalidTrimRangesAreRejected(string start, string end)
    {
        Assert.False(RecordingUtilities.TryParseTrimRange(start, end, out _, out _));
    }

    [Fact]
    public void RegionPickerPreservesNegativeVirtualDesktopCoordinates()
    {
        var result = RecordingUtilities.NormalizeRegion(20, 30, 220, 130, -1920, 0);
        Assert.Equal(new Rectangle(-1900, 30, 200, 100), result);
    }

    [Fact]
    public void RegionPickerNormalizesReverseDragDirection()
    {
        var result = RecordingUtilities.NormalizeRegion(220, 130, 20, 30, 0, -1080);
        Assert.Equal(new Rectangle(20, -1050, 200, 100), result);
    }

    [Theory]
    [InlineData("capture.mp4", true)]
    [InlineData("capture.MKV", true)]
    [InlineData("image.png", false)]
    public void VideoExtensionCheckIsCaseInsensitive(string path, bool expected)
    {
        Assert.Equal(expected, RecordingUtilities.IsSupportedVideoExtension(path));
    }
}
