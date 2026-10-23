using System.Drawing;
using System.Windows.Forms;

namespace NovaScreenRecorder.App;

/// <summary>Per-monitor-DPI-aware selection overlay. Coordinates returned are virtual-desktop physical pixels.</summary>
internal sealed class RegionPickerForm : Form
{
    private readonly Point _virtualOrigin;
    private Point _start;
    private Point _end;
    private bool _dragging;
    public Rectangle? SelectedBounds { get; private set; }

    public RegionPickerForm()
    {
        var virtualBounds = SystemInformation.VirtualScreen;
        _virtualOrigin = virtualBounds.Location;
        Bounds = virtualBounds;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.35;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _start = e.Location; _end = e.Location; _dragging = true; Invalidate(); } };
        MouseMove += (_, e) => { if (_dragging) { _end = e.Location; Invalidate(); } };
        MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !_dragging) return;
            _end = e.Location; _dragging = false;
            var local = Normalize(_start, _end);
            if (local.Width >= 16 && local.Height >= 16)
            {
                SelectedBounds = RecordingUtilities.NormalizeRegion(_start.X, _start.Y, _end.X, _end.Y, _virtualOrigin.X, _virtualOrigin.Y);
                DialogResult = DialogResult.OK;
            }
            else DialogResult = DialogResult.Cancel;
            Close();
        };
        Paint += (_, e) =>
        {
            using var pen = new Pen(Color.DeepSkyBlue, 3);
            using var fill = new SolidBrush(Color.FromArgb(40, 0, 190, 255));
            var rect = Normalize(_start, _end);
            if (_dragging || rect.Width > 0) { e.Graphics.FillRectangle(fill, rect); e.Graphics.DrawRectangle(pen, rect); }
            using var font = new Font("Segoe UI", 16, FontStyle.Bold);
            e.Graphics.DrawString("Drag to select a region  •  Esc to cancel", font, Brushes.White, 24, 24);
        };
    }

    private static Rectangle Normalize(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
