using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace S1Utility.Controls;

// A circular knob control drawn entirely with Avalonia's drawing API.
// Drag upward to increase the value, downward to decrease it.
// The colored arc shows how far along the 0-127 range the current value is.
public class RotaryKnob : Control
{
    // Physical layout constants (all in pixels)
    private const double Size           = 56;   // total width and height of the control
    private const double Cx             = Size / 2;   // 28 — knob centre X
    private const double Cy             = Size / 2;   // 28 — knob centre Y
    private const double BodyRadius     = 18.0;  // dark filled circle
    private const double TrackRadius    = 24.0;  // the arc ring sits here
    private const double IndicatorInner = 4.0;   // indicator line starts here (from centre)
    private const double IndicatorOuter = 14.0;  // indicator line ends here

    // The knob sweeps from 7 o'clock to 5 o'clock — 300° of travel.
    // Angles are measured clockwise from 3 o'clock (standard screen convention).
    private const double StartAngleDeg = 120.0;   // 7 o'clock
    private const double TotalSweepDeg = 300.0;

    // ── Avalonia styled properties ────────────────────────────────────────────

    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<RotaryKnob, int>(nameof(Value), defaultValue: 64);

    // The accent colour used for the filled value arc.
    // Leave null to fall back to orange.
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<RotaryKnob, IBrush?>(nameof(AccentBrush));

    // When false the knob renders desaturated (gray arc + dim indicator) to
    // signal that the editor value may not match what is loaded on the synth.
    public static readonly StyledProperty<bool> IsSyncedProperty =
        AvaloniaProperty.Register<RotaryKnob, bool>(nameof(IsSynced), defaultValue: false);

    // The lowest CC value this knob can produce. Values below MinValue are
    // clamped and the arc treats MinValue as the visual zero position.
    public static readonly StyledProperty<int> MinValueProperty =
        AvaloniaProperty.Register<RotaryKnob, int>(nameof(MinValue), defaultValue: 0);

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, 0, 127));
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public bool IsSynced
    {
        get => GetValue(IsSyncedProperty);
        set => SetValue(IsSyncedProperty, value);
    }

    public int MinValue
    {
        get => GetValue(MinValueProperty);
        set => SetValue(MinValueProperty, Math.Clamp(value, 0, 126));
    }

    // Raised whenever Value changes so callers can update their data model.
    public event EventHandler<int>? ValueChanged;

    // ── Cached brushes/pens (Render runs at 60fps; avoid per-frame allocations) ──

    private static readonly IBrush s_defaultAccent   = new SolidColorBrush(Color.Parse("#F0A040"));
    private static readonly IBrush s_unsyncedAccent  = new SolidColorBrush(Color.Parse("#484848"));
    private static readonly IBrush s_bodyFill        = new SolidColorBrush(Color.Parse("#2C2C2C"));
    private static readonly IPen   s_bodyRimSynced   = new Pen(new SolidColorBrush(Color.Parse("#5A5A5A")), 1.5);
    private static readonly IPen   s_bodyRimUnsynced = new Pen(new SolidColorBrush(Color.Parse("#404040")), 1.5);
    private static readonly IPen   s_trackPen        = new Pen(new SolidColorBrush(Color.Parse("#3C3C3C")), 3, lineCap: PenLineCap.Round);
    private static readonly IPen   s_indicatorSynced   = new Pen(new SolidColorBrush(Color.Parse("#FFFFFF")), 1.5, lineCap: PenLineCap.Round);
    private static readonly IPen   s_indicatorUnsynced = new Pen(new SolidColorBrush(Color.Parse("#505050")), 1.5, lineCap: PenLineCap.Round);

    // The full background-track arc geometry never depends on the value; build once.
    private static readonly StreamGeometry s_trackGeometry = BuildArcGeometry(
        new Point(Cx, Cy), TrackRadius, StartAngleDeg, TotalSweepDeg);

    // ── Drag state ────────────────────────────────────────────────────────────

    private bool   _isDragging;
    private double _dragStartY;
    private int    _dragStartValue;

    public RotaryKnob()
    {
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size _) => new(Size, Size);

    // ── Property change → redraw ──────────────────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            InvalidateVisual();
            ValueChanged?.Invoke(this, Value);
        }
        else if (change.Property == AccentBrushProperty || change.Property == IsSyncedProperty
                                                        || change.Property == MinValueProperty)
        {
            InvalidateVisual();
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext dc)
    {
        var center  = new Point(Cx, Cy);
        bool synced = IsSynced;

        IBrush accent = synced
            ? (AccentBrush ?? s_defaultAccent)
            : s_unsyncedAccent;

        // 1. Knob body — dark filled circle; rim slightly dimmer when unsynced
        dc.DrawEllipse(s_bodyFill, synced ? s_bodyRimSynced : s_bodyRimUnsynced,
            center, BodyRadius, BodyRadius);

        // 2. Background track arc — shows the full 300° travel range (cached geometry)
        dc.DrawGeometry(null, s_trackPen, s_trackGeometry);

        // 3. Value arc — normalized so MinValue renders at the visual zero position
        int    minV       = MinValue;
        double valueSweep = (127 - minV) > 0
            ? Math.Max(0, Value - minV) / (double)(127 - minV) * TotalSweepDeg
            : 0;
        if (valueSweep > 0.5)
        {
            // Value-arc pen still depends on AccentBrush, which is per-instance configurable.
            DrawArc(dc,
                new Pen(accent, 3, lineCap: PenLineCap.Round),
                center, TrackRadius, StartAngleDeg, valueSweep);
        }

        // 4. Indicator line — dim when unsynced
        double indicatorRad = (StartAngleDeg + valueSweep) * Math.PI / 180.0;
        var innerPt = new Point(
            Cx + IndicatorInner * Math.Cos(indicatorRad),
            Cy + IndicatorInner * Math.Sin(indicatorRad));
        var outerPt = new Point(
            Cx + IndicatorOuter * Math.Cos(indicatorRad),
            Cy + IndicatorOuter * Math.Sin(indicatorRad));
        dc.DrawLine(synced ? s_indicatorSynced : s_indicatorUnsynced, innerPt, outerPt);
    }

    // Draws a circular arc using StreamGeometry.
    // startDeg and sweepDeg follow screen-space convention (0° = 3 o'clock, clockwise positive).
    private static void DrawArc(DrawingContext dc, IPen pen,
        Point center, double radius, double startDeg, double sweepDeg)
    {
        if (sweepDeg < 0.1) return;
        dc.DrawGeometry(null, pen, BuildArcGeometry(center, radius, startDeg, sweepDeg));
    }

    private static StreamGeometry BuildArcGeometry(
        Point center, double radius, double startDeg, double sweepDeg)
    {
        var startRad = startDeg * Math.PI / 180.0;
        var endRad   = (startDeg + sweepDeg) * Math.PI / 180.0;

        var startPt = new Point(center.X + radius * Math.Cos(startRad),
                                center.Y + radius * Math.Sin(startRad));
        var endPt   = new Point(center.X + radius * Math.Cos(endRad),
                                center.Y + radius * Math.Sin(endRad));

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(startPt, isFilled: false);
            ctx.ArcTo(endPt, new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: sweepDeg > 180,
                sweepDirection: SweepDirection.Clockwise);
        }
        return geo;
    }

    // ── Mouse drag interaction ────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging    = true;
            _dragStartY    = e.GetPosition(this).Y;
            _dragStartValue = Value;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_isDragging) return;
        // Dragging up (negative delta) increases value; down decreases it.
        var delta = _dragStartY - e.GetPosition(this).Y;
        Value = Math.Clamp(_dragStartValue + (int)(delta * 0.8), MinValue, 127);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}
