using AreaRec.Core.Recording;

namespace AreaRec.App;

internal sealed class RegionSelectorForm : Form
{
    private readonly Action<PhysicalRegion> _selected;
    private readonly Action _cancelled;
    private readonly Font _instructionFont = new("Segoe UI", 12, FontStyle.Bold);
    private readonly Font _measurementFont = new("Segoe UI", 10, FontStyle.Bold);
    private Point _startScreen;
    private PhysicalRegion? _selection;
    private bool _dragging;
    private bool _completed;

    public RegionSelectorForm(Action<PhysicalRegion> selected, Action cancelled)
    {
        _selected = selected ?? throw new ArgumentNullException(nameof(selected));
        _cancelled = cancelled ?? throw new ArgumentNullException(nameof(cancelled));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Opacity = 0.32;
        Bounds = SystemInformation.VirtualScreen;

        KeyDown += OnKeyDown;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var instruction = "DRAG TO SELECT · ESC TO CANCEL";
        var instructionSize = e.Graphics.MeasureString(instruction, _instructionFont);
        using var instructionBrush = new SolidBrush(Color.White);
        e.Graphics.DrawString(
            instruction,
            _instructionFont,
            instructionBrush,
            (ClientSize.Width - instructionSize.Width) / 2,
            24);

        if (_selection is not { } region)
        {
            return;
        }

        var topLeft = PointToClient(new Point(region.X, region.Y));
        var rectangle = new Rectangle(topLeft, new Size(region.Width, region.Height));
        using var pen = new Pen(Color.Lime, 3);
        using var measurementBrush = new SolidBrush(Color.Lime);
        e.Graphics.DrawRectangle(pen, rectangle);
        e.Graphics.DrawString(
            $"{region.Width}×{region.Height}  ({region.X}, {region.Y})",
            _measurementFont,
            measurementBrush,
            rectangle.Left + 6,
            Math.Max(4, rectangle.Top - 26));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            CompleteCancelled();
        }
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = true;
        _startScreen = PointToScreen(e.Location);
        _selection = null;
        Invalidate();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var current = PointToScreen(e.Location);
        _selection = PhysicalRegion.FromPoints(_startScreen.X, _startScreen.Y, current.X, current.Y);
        Invalidate();
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = false;
        var current = PointToScreen(e.Location);
        var region = PhysicalRegion.FromPoints(_startScreen.X, _startScreen.Y, current.X, current.Y);
        if (region.IsValid)
        {
            _selection = region;
            CompleteSelected(region);
        }
        else
        {
            CompleteCancelled();
        }
    }

    private void CompleteSelected(PhysicalRegion region)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _selected(region);
        Close();
    }

    private void CompleteCancelled()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _cancelled();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _instructionFont.Dispose();
            _measurementFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
