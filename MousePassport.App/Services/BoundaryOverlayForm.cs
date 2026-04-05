using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using MousePassport.App.Models;

namespace MousePassport.App.Services;

/// <summary>
/// Full virtual-screen overlay that draws pass-through segments on real monitor seams.
/// Includes nodes, tick marks, leader lines, and callouts. Click-through.
/// </summary>
public sealed class BoundaryOverlayForm : Form
{
    private const int WsExLayered = 0x80000;
    private const int WsExTransparent = 0x20;

    private const float NodeRadius = 5.5f;
    private const float TickHalf = 8f;
    private const float CalloutOffset = 18f;
    private const float LeaderGap = 5f;
    private const float CalloutPad = 7f;
    private const float CalloutCorner = 7f;

    private readonly Pen _blockedPen;
    private readonly Pen _passPen;
    private readonly Pen _tickPen;
    private readonly Pen _leaderPen;
    private readonly Pen _calloutBorderPen;
    private readonly SolidBrush _nodeFill;
    private readonly Pen _nodeRingPen;
    private readonly SolidBrush _calloutFill;
    private readonly SolidBrush _labelBrush;
    private readonly Font _labelFont;

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

        _blockedPen = new Pen(Color.FromArgb(209, 52, 56), 2f)
        {
            DashStyle = DashStyle.Dash,
            LineJoin = LineJoin.Round
        };
        _passPen = new Pen(Color.FromArgb(16, 124, 16), 4.5f)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        _tickPen = new Pen(Color.FromArgb(16, 124, 16), 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        _leaderPen = new Pen(Color.FromArgb(210, 16, 124, 16), 1.5f)
        {
            DashStyle = DashStyle.Dash,
            DashPattern = [3f, 2.5f]
        };
        _calloutBorderPen = new Pen(Color.FromArgb(200, 16, 124, 16), 1f);
        _nodeFill = new SolidBrush(Color.FromArgb(16, 124, 16));
        _nodeRingPen = new Pen(Color.FromArgb(245, 255, 255, 255), 2f);
        _calloutFill = new SolidBrush(Color.FromArgb(235, 232, 245, 233));
        _labelBrush = new SolidBrush(Color.FromArgb(250, 10, 80, 10));
        _labelFont = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
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
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
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
            DrawPortAnnotations(g, vs, edge, start, end);
        }
    }

    private void DrawPortAnnotations(Graphics g, Rectangle vs, SharedEdge edge, int start, int end)
    {
        var span = Math.Abs(end - start);
        var label = $"Pass · {span} px";

        if (edge.Orientation == EdgeOrientation.Vertical)
        {
            var x = edge.ConstantCoordinate - vs.X + 0.5f;
            var y1 = start - vs.Y + 0.5f;
            var y2 = end - vs.Y + 0.5f;
            var midY = (y1 + y2) * 0.5f;

            g.DrawLine(_tickPen, x - TickHalf, y1, x + TickHalf, y1);
            g.DrawLine(_tickPen, x - TickHalf, y2, x + TickHalf, y2);

            DrawNode(g, x, y1);
            DrawNode(g, x, y2);

            var sz = g.MeasureString(label, _labelFont);
            var cw = sz.Width + CalloutPad * 2;
            var ch = sz.Height + CalloutPad * 2;
            var boxLeft = x + CalloutOffset;
            var boxTop = midY - ch * 0.5f;
            var boxRect = new RectangleF(boxLeft, boxTop, cw, ch);

            var leaderEndX = boxLeft - LeaderGap;
            g.DrawLine(_leaderPen, x, midY, leaderEndX, midY);

            FillRoundedRectangle(g, _calloutFill, boxRect, CalloutCorner);
            using (var outline = CreateRoundedRectPath(boxRect, CalloutCorner))
            {
                g.DrawPath(_calloutBorderPen, outline);
            }

            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(label, _labelFont, _labelBrush, boxRect, format);
        }
        else
        {
            var y = edge.ConstantCoordinate - vs.Y + 0.5f;
            var x1 = start - vs.X + 0.5f;
            var x2 = end - vs.X + 0.5f;
            var midX = (x1 + x2) * 0.5f;

            g.DrawLine(_tickPen, x1, y - TickHalf, x1, y + TickHalf);
            g.DrawLine(_tickPen, x2, y - TickHalf, x2, y + TickHalf);

            DrawNode(g, x1, y);
            DrawNode(g, x2, y);

            var sz = g.MeasureString(label, _labelFont);
            var cw = sz.Width + CalloutPad * 2;
            var ch = sz.Height + CalloutPad * 2;
            var boxLeft = midX - cw * 0.5f;
            var boxTop = y + CalloutOffset;
            var boxRect = new RectangleF(boxLeft, boxTop, cw, ch);

            var leaderEndY = boxTop - LeaderGap;
            g.DrawLine(_leaderPen, midX, y, midX, leaderEndY);

            FillRoundedRectangle(g, _calloutFill, boxRect, CalloutCorner);
            using (var outline = CreateRoundedRectPath(boxRect, CalloutCorner))
            {
                g.DrawPath(_calloutBorderPen, outline);
            }

            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString(label, _labelFont, _labelBrush, boxRect, format);
        }
    }

    private void DrawNode(Graphics g, float x, float y)
    {
        var d = NodeRadius * 2f;
        var rect = new RectangleF(x - NodeRadius, y - NodeRadius, d, d);
        g.FillEllipse(_nodeFill, rect);
        g.DrawEllipse(_nodeRingPen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private static void FillRoundedRectangle(Graphics g, Brush brush, RectangleF bounds, float radius)
    {
        using var path = CreateRoundedRectPath(bounds, radius);
        g.FillPath(brush, path);
    }

    private static GraphicsPath CreateRoundedRectPath(RectangleF b, float r)
    {
        var path = new GraphicsPath();
        var d = Math.Min(r * 2, Math.Min(b.Width, b.Height));
        var arc = d * 0.5f;
        path.AddArc(b.X, b.Y, d, d, 180, 90);
        path.AddArc(b.Right - d, b.Y, d, d, 270, 90);
        path.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
        path.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
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
            _tickPen.Dispose();
            _leaderPen.Dispose();
            _calloutBorderPen.Dispose();
            _nodeFill.Dispose();
            _nodeRingPen.Dispose();
            _calloutFill.Dispose();
            _labelBrush.Dispose();
            _labelFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
