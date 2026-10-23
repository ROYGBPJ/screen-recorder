using System.Drawing;
using System.IO;

namespace NovaScreenRecorder.App;

/// <summary>Small deterministic helpers shared by the UI and automated tests.</summary>
public static class RecordingUtilities
{
    public static string SelectEncoder(int preference, IEnumerable<string> availableEncoders, bool forceCpu = false, IEnumerable<string>? excludedEncoders = null)
    {
        var available = new HashSet<string>(availableEncoders, StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(excludedEncoders ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var cpuCandidates = new[] { "h264_mf", "libx264", "mpeg4" };
        if (forceCpu || preference == 1)
            return cpuCandidates.FirstOrDefault(e => available.Contains(e) && !excluded.Contains(e)) ?? "mpeg4";
        if (preference == 0)
            return new[] { "h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libx264", "mpeg4" }
                .FirstOrDefault(e => available.Contains(e) && !excluded.Contains(e)) ?? "mpeg4";
        var requested = preference switch { 2 => "h264_nvenc", 3 => "h264_qsv", 4 => "h264_amf", _ => "mpeg4" };
        if (available.Contains(requested) && !excluded.Contains(requested)) return requested;
        return cpuCandidates.FirstOrDefault(e => available.Contains(e) && !excluded.Contains(e)) ?? "mpeg4";
    }

    public static bool TryParseTrimRange(string startText, string endText, out TimeSpan start, out TimeSpan duration)
    {
        start = TimeSpan.Zero; duration = TimeSpan.Zero;
        if (!TimeSpan.TryParse(startText, out var parsedStart) || !TimeSpan.TryParse(endText, out var parsedEnd)) return false;
        if (parsedStart < TimeSpan.Zero || parsedEnd <= parsedStart) return false;
        start = parsedStart; duration = parsedEnd - parsedStart;
        return true;
    }

    public static Rectangle NormalizeRegion(int startX, int startY, int endX, int endY, int virtualOriginX, int virtualOriginY)
    {
        var x = Math.Min(startX, endX); var y = Math.Min(startY, endY);
        return new Rectangle(x + virtualOriginX, y + virtualOriginY, Math.Abs(endX - startX), Math.Abs(endY - startY));
    }

    public static bool IsSupportedVideoExtension(string path) => new[] { ".mp4", ".mkv", ".mov", ".avi", ".webm" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
