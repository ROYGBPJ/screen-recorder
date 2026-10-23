using System.Globalization;
using System.IO;
using System.Text;

namespace NovaScreenRecorder.App;

/// <summary>
/// Local, opt-out diagnostics. Logs stay on the user's PC and are never uploaded.
/// Files are retained until the user manually deletes them.
/// </summary>
internal static class ErrorLogService
{
    private static readonly object Gate = new();
    private static bool _enabled = true;
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovaScreenRecorder", "Error");

    public static bool Enabled
    {
        get { lock (Gate) return _enabled; }
        set { lock (Gate) _enabled = value; }
    }

    public static string FolderPath => Root;

    public static void Initialize(bool enabled = true)
    {
        Enabled = enabled;
        if (enabled)
            Write("INFO", "Diagnostics enabled. Logs are stored locally and retained until manually deleted.");
    }

    public static void WriteException(string area, Exception exception, string? extra = null)
    {
        if (!Enabled || exception is null) return;
        var message = new StringBuilder()
            .AppendLine($"Area: {area}")
            .AppendLine($"Exception: {exception.GetType().FullName}")
            .AppendLine($"Message: {exception.Message}")
            .AppendLine("Stack trace:")
            .AppendLine(exception.ToString());
        if (!string.IsNullOrWhiteSpace(extra)) message.AppendLine("Details:").AppendLine(extra);
        Write("ERROR", message.ToString());
    }

    public static void Write(string level, string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Root);
                string file = Path.Combine(Root, $"Nova-Error-{DateTime.Now:yyyy-MM-dd}.log");
                string entry = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}] [{level}] {message}{Environment.NewLine}{new string('-', 78)}{Environment.NewLine}";
                File.AppendAllText(file, entry, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never crash the recorder or create an exception loop.
        }
    }
}
