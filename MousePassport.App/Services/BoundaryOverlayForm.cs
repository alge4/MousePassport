using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MousePassport.App.Models;

namespace MousePassport.App.Services;

/// <summary>
/// Full virtual-screen overlay that draws pass-through segments on real monitor edges.
/// Click-through so the setup window and desktop remain usable underneath.
/// </summary>
public sealed class BoundaryOverlayForm : Form
{
    private const int WsExLayered = 0x80000;
    private const int WsExTransparent = 0x20;

    private readonly Pen _blockedPen;
    private readonly Pen _passPen;

    private IReadOnlyList<SharedEdge> _edges = [];
    private IReadOnlyDictionary<string, EdgePort> _ports =
        new Dictionary<string, EdgePort>(StringComparer.Ordinal);

    public BoundaryOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        var vs = SystemInformation.VirtualScreen;
        Location = vs.Location;
        Size = vs.Size;

        BackColor = Color.Fuchsia;
        TransparencyKey = Color.Fuchsia;

        _blockedPen = new Pen(Color.FromArgb(220, 72, 72), 2f)
        {
            DashStyle = DashStyle.Dash,
            LineJoin = LineJoin.Round
        };
        _passPen = new Pen(Color.FromArgb(34, 168, 90), 6f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Flat,
            EndCap = LineCap.Flat
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered | WsExTransparent;
            return cp;
        }
    }

    public void SetGeometry(
        IReadOnlyList<SharedEdge> edges,
        IReadOnlyDictionary<string, EdgePort> ports)
    {
        _edges = edges ?? [];
        _ports = ports ?? new Dictionary<string, EdgePort>(StringComparer.Ordinal);
        if (!IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(Invalidate));
        }
        else
        {
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var vs = SystemInformation.VirtualScreen;

        foreach (var edge in _edges)
        {
            if (!_ports.TryGetValue(edge.Id, out var port))
            {
                continue;
            }

            var start = Math.Min(port.PortStart, port.PortEnd);
            var end = Math.Max(port.PortStart, port.PortEnd);

            DrawEdgeSegment(g, vs, edge, edge.SegmentStart, edge.SegmentEnd, _blockedPen);
            DrawEdgeSegment(g, vs, edge, start, end, _passPen);
        }
    }

    private static void DrawEdgeSegment(Graphics g, Rectangle vs, SharedEdge edge, int a, int b, Pen pen)
    {
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);

        if (edge.Orientation == EdgeOrientation.Vertical)
        {
            var x = edge.ConstantCoordinate - vs.X + 0.5f;
            var y1 = lo - vs.Y + 0.5f;
            var y2 = hi - vs.Y + 0.5f;
            g.DrawLine(pen, x, y1, x, y2);
        }
        else
        {
            var y = edge.ConstantCoordinate - vs.Y + 0.5f;
            var x1 = lo - vs.X + 0.5f;
            var x2 = hi - vs.X + 0.5f;
            g.DrawLine(pen, x1, y, x2, y);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _blockedPen.Dispose();
            _passPen.Dispose();
        }

        base.Dispose(disposing);
    }
}
