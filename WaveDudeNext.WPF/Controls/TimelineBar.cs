// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alexander Graef <alex@graef.cc>
//
// This file is part of WaveDude Next, a 2D wave simulator.
//
// Based on the work of
//   Jeroen Vleggaar (Huygens Optics), author of Wave Dude 2D, Copyright (C) 2026
//     <https://www.youtube.com/@HuygensOptics>, <https://www.huygensoptics.com>
//   0x23 (Diffraction Limited), author of WaveSimulator2D
//     <https://www.youtube.com/@DiffractionLimited>, <https://github.com/0x23/WaveSimulator2D>
//
// WaveDude Next is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. It is distributed WITHOUT ANY WARRANTY. See the LICENSE.md file or
// <https://www.gnu.org/licenses/>.

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;

namespace WaveDudeNext.WPF.Controls;

/// <summary>
/// The time axis of a run, in frames: a tall scrub bar with tick labels, a playhead with its
/// frame number, an in/out range drawn as a band between two brackets, and the frame under the
/// mouse. Click or drag scrubs; dragging a bracket asks for a new in or out point through
/// <see cref="SetRangeStartCommand"/> / <see cref="SetRangeEndCommand"/> (the range itself is
/// owned by the model, so the control never writes <see cref="RangeStart"/> or
/// <see cref="RangeEnd"/>). Keyboard: Left/Right one frame, PageUp/PageDown ten, Home/End to
/// the in/out points, the wheel one frame. While a run is in progress <see cref="Available"/> shades the
/// frames that exist and bounds the playhead. Drawn in <see cref="OnRender"/>; colours come from
/// the brush properties so a theme style can set them.
/// </summary>
public sealed class TimelineBar : FrameworkElement
{
	/// <summary>Height of the label row above the bar, in pixels.</summary>
	public const double LabelRowHeight = 14.0;

	/// <summary>Default height of the whole control, in pixels.</summary>
	public const double DefaultHeight = 36.0;

	/// <summary>Pixels a tick label needs at least, which picks the labelled tick interval.</summary>
	private const double MinLabelSpacing = 64.0;

	/// <summary>Half-width of the hit area of a range bracket, in pixels.</summary>
	private const double BracketHitHalfWidth = 6.0;

	private static readonly int[] TickCandidates = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000];
	private static readonly Typeface LabelTypeface = new("Segoe UI");

	/// <summary>Identifies the <see cref="Maximum"/> property: the last frame index.</summary>
	public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
		nameof(Maximum), typeof(int), typeof(TimelineBar),
		new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, OnMaximumChanged));

	/// <summary>Identifies the <see cref="Value"/> property: the frame shown.</summary>
	public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
		nameof(Value), typeof(int), typeof(TimelineBar),
		new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
			OnValueChanged, CoerceValue));

	/// <summary>Identifies the <see cref="RangeStart"/> property: the in point, in frames.</summary>
	public static readonly DependencyProperty RangeStartProperty = DependencyProperty.Register(
		nameof(RangeStart), typeof(int), typeof(TimelineBar), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>Identifies the <see cref="RangeEnd"/> property: the out point, in frames.</summary>
	public static readonly DependencyProperty RangeEndProperty = DependencyProperty.Register(
		nameof(RangeEnd), typeof(int), typeof(TimelineBar), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>Identifies the <see cref="SetRangeStartCommand"/> property.</summary>
	public static readonly DependencyProperty SetRangeStartCommandProperty = DependencyProperty.Register(
		nameof(SetRangeStartCommand), typeof(ICommand), typeof(TimelineBar));

	/// <summary>Identifies the <see cref="SetRangeEndCommand"/> property.</summary>
	public static readonly DependencyProperty SetRangeEndCommandProperty = DependencyProperty.Register(
		nameof(SetRangeEndCommand), typeof(ICommand), typeof(TimelineBar));

	/// <summary>Identifies the <see cref="Background"/> property: fill of the bar.</summary>
	public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
		nameof(Background), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>Identifies the <see cref="Foreground"/> property: playhead and its label.</summary>
	public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
		nameof(Foreground), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>Identifies the <see cref="TickBrush"/> property: ticks, bar border and hover line.</summary>
	public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
		nameof(TickBrush), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>Identifies the <see cref="LabelBrush"/> property: tick labels and the hover label.</summary>
	public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
		nameof(LabelBrush), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>Identifies the <see cref="AccentBrush"/> property: range band, brackets and keyboard focus.</summary>
	public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
		nameof(AccentBrush), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

	/// <summary>Identifies the <see cref="Available"/> property: frames computed so far.</summary>
	public static readonly DependencyProperty AvailableProperty = DependencyProperty.Register(
		nameof(Available), typeof(int), typeof(TimelineBar),
		new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => d.CoerceValue(ValueProperty)));

	/// <summary>Identifies the <see cref="AvailableBrush"/> property: shading of the available frames.</summary>
	public static readonly DependencyProperty AvailableBrushProperty = DependencyProperty.Register(
		nameof(AvailableBrush), typeof(Brush), typeof(TimelineBar), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

	private enum DragMode { None, Scrub, RangeStart, RangeEnd }

	private DragMode _drag;
	private int? _hoverFrame;

	public TimelineBar()
	{
		Focusable = true;
		FocusVisualStyle = null;
		SnapsToDevicePixels = true;
		Height = DefaultHeight;
	}

	/// <summary>Last frame index; 0 means a single frame and nothing to scrub.</summary>
	public int Maximum
	{
		get => (int)GetValue(MaximumProperty);
		set => SetValue(MaximumProperty, value);
	}

	/// <summary>Frame shown, 0..<see cref="Maximum"/>.</summary>
	public int Value
	{
		get => (int)GetValue(ValueProperty);
		set => SetValue(ValueProperty, value);
	}

	/// <summary>In point of the range, in frames.</summary>
	public int RangeStart
	{
		get => (int)GetValue(RangeStartProperty);
		set => SetValue(RangeStartProperty, value);
	}

	/// <summary>Out point of the range, in frames.</summary>
	public int RangeEnd
	{
		get => (int)GetValue(RangeEndProperty);
		set => SetValue(RangeEndProperty, value);
	}

	/// <summary>Executed with the frame (int) when the in bracket is dragged.</summary>
	public ICommand? SetRangeStartCommand
	{
		get => (ICommand?)GetValue(SetRangeStartCommandProperty);
		set => SetValue(SetRangeStartCommandProperty, value);
	}

	/// <summary>Executed with the frame (int) when the out bracket is dragged.</summary>
	public ICommand? SetRangeEndCommand
	{
		get => (ICommand?)GetValue(SetRangeEndCommandProperty);
		set => SetValue(SetRangeEndCommandProperty, value);
	}

	/// <summary>Fill of the bar.</summary>
	public Brush Background
	{
		get => (Brush)GetValue(BackgroundProperty);
		set => SetValue(BackgroundProperty, value);
	}

	/// <summary>Playhead and its frame label.</summary>
	public Brush Foreground
	{
		get => (Brush)GetValue(ForegroundProperty);
		set => SetValue(ForegroundProperty, value);
	}

	/// <summary>Ticks, the bar's border and the hover line.</summary>
	public Brush TickBrush
	{
		get => (Brush)GetValue(TickBrushProperty);
		set => SetValue(TickBrushProperty, value);
	}

	/// <summary>Tick labels and the hover label.</summary>
	public Brush LabelBrush
	{
		get => (Brush)GetValue(LabelBrushProperty);
		set => SetValue(LabelBrushProperty, value);
	}

	/// <summary>Range band and brackets, keyboard focus border.</summary>
	public Brush AccentBrush
	{
		get => (Brush)GetValue(AccentBrushProperty);
		set => SetValue(AccentBrushProperty, value);
	}

	/// <summary>
	/// Number of frames that exist so far while a run fills the axis; 0 when every frame of the axis is
	/// there (no run). The bar shades them; whether <see cref="Value"/> may go beyond is the owner's call.
	/// </summary>
	public int Available
	{
		get => (int)GetValue(AvailableProperty);
		set => SetValue(AvailableProperty, value);
	}

	/// <summary>Shading of the available frames (a translucent fill works best).</summary>
	public Brush AvailableBrush
	{
		get => (Brush)GetValue(AvailableBrushProperty);
		set => SetValue(AvailableBrushProperty, value);
	}

	private static void OnMaximumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => d.CoerceValue(ValueProperty);

	private static object CoerceValue(DependencyObject d, object value) => Math.Clamp((int)value, 0, ((TimelineBar)d).Maximum);

	private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (UIElementAutomationPeer.FromElement((TimelineBar)d) is TimelineBarAutomationPeer peer)
		{
			peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, (double)(int)e.OldValue, (double)(int)e.NewValue);
		}
	}

	/// <inheritdoc/>
	protected override AutomationPeer OnCreateAutomationPeer() => new TimelineBarAutomationPeer(this);

	/// <inheritdoc/>
	protected override Size MeasureOverride(Size availableSize) =>
		new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, DefaultHeight);

	// ---------------------------------------------------------------- geometry

	/// <summary>
	/// Usable track: frame 0 on the first pixel column inside the bar's outline, the last frame on the last one
	/// (columns 1 to width − 2), so the playhead and the brackets at either end stay off the outline.
	/// </summary>
	private double TrackLeft => 1.0;
	private double TrackWidth => Math.Max(1.0, ActualWidth - 3.0);
	private double BarTop => LabelRowHeight;
	private double BarHeight => Math.Max(1.0, ActualHeight - LabelRowHeight);

	/// <summary>Pixel x of a frame's centre line.</summary>
	private double XOf(int frame) => Maximum > 0 ? TrackLeft + frame * TrackWidth / Maximum : TrackLeft;

	/// <summary>Frame nearest to a pixel x, clamped.</summary>
	private int FrameAt(double x) =>
		Maximum > 0 ? Math.Clamp((int)Math.Round((x - TrackLeft) / TrackWidth * Maximum), 0, Maximum) : 0;

	/// <summary>Which bracket, if any, a pixel x is on; the nearer one wins when both are close.</summary>
	private DragMode BracketAt(double x)
	{
		double dStart = Math.Abs(x - XOf(RangeStart));
		double dEnd = Math.Abs(x - XOf(RangeEnd));
		if (dStart <= BracketHitHalfWidth && dStart <= dEnd)
		{
			return DragMode.RangeStart;
		}
		return dEnd <= BracketHitHalfWidth ? DragMode.RangeEnd : DragMode.None;
	}

	// ---------------------------------------------------------------- rendering

	/// <inheritdoc/>
	protected override void OnRender(DrawingContext dc)
	{
		double w = ActualWidth;
		double h = ActualHeight;
		if (w <= 2 || h <= LabelRowHeight)
		{
			return;
		}
		_dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; // once per draw, not per label
		Pen pen = PenFor(TickBrush);
		var bar = new Rect(0.5, BarTop + 0.5, w - 1.0, BarHeight - 1.0);
		dc.DrawRectangle(Background, pen, bar);
		// fills and ticks lie on whole pixels inside the outline, whose rectangle runs through the centres of its pixels:
		// on those half-pixel edges a fill is smeared over one row or column more (2026-11-01, the in point's bracket)
		var inner = new Rect(1.0, BarTop + 1.0, w - 2.0, BarHeight - 2.0);

		// the frames a run has produced so far, as a band from the start to the newest frame's column
		if (Available > 0 && Available <= Maximum)
		{
			double xa = Math.Round(XOf(Available - 1)) + 1.0;
			dc.DrawRectangle(AvailableBrush, null, new Rect(inner.Left, inner.Top, Math.Max(0, xa - inner.Left), inner.Height));
		}

		// the in/out range: a translucent band over both points' columns once it is narrower than the run
		double c0 = Math.Round(XOf(RangeStart));
		double c1 = Math.Round(XOf(RangeEnd));
		if (c1 > c0 && (RangeStart > 0 || RangeEnd < Maximum))
		{
			dc.PushOpacity(0.25);
			dc.DrawRectangle(AccentBrush, null, new Rect(c0, inner.Top, c1 + 1.0 - c0, inner.Height));
			dc.Pop();
		}

		// ticks and labels, from a cached drawing (2026-10-16): laid out afresh they cost over a millisecond per draw,
		// and the bar is drawn on every position change, sixty times a second during playback; the drawing is rebuilt
		// only when the width, the range, the brushes, the culture or the DPI change
		if (Maximum > 0)
		{
			dc.DrawDrawing(TicksDrawing(w, inner, pen));
		}

		// the brackets over the ticks: a tick on a point's frame (frame 0 always has one) cut a notch into its bracket
		DrawBracket(dc, c0, inner, open: true);
		DrawBracket(dc, c1, inner, open: false);

		// the frame under the mouse
		if (_hoverFrame is int hover && _drag == DragMode.None && hover != Value)
		{
			double x = Math.Round(XOf(hover)) + 0.5;
			dc.PushOpacity(0.5);
			dc.DrawLine(pen, new Point(x, bar.Top), new Point(x, bar.Bottom));
			dc.Pop();
		}

		// the playhead: a line through the bar, a handle on top, the frame number next to it
		double px = Math.Round(XOf(Value)) + 0.5;
		dc.DrawLine(PenFor(Foreground), new Point(px, bar.Top), new Point(px, bar.Bottom));
		var handle = new StreamGeometry();
		using (StreamGeometryContext g = handle.Open())
		{
			g.BeginFigure(new Point(px - 5, bar.Top - 6), true, true);
			g.LineTo(new Point(px + 5, bar.Top - 6), false, false);
			g.LineTo(new Point(px, bar.Top), false, false);
		}
		dc.DrawGeometry(Foreground, null, handle);
		FormattedText playhead = Label(Value.ToString(CultureInfo.CurrentCulture), Foreground, bold: true);
		double tx = px + 8 + playhead.Width <= w ? px + 8 : px - 8 - playhead.Width;
		dc.DrawRectangle(Background, null, new Rect(tx - 3, 0, playhead.Width + 6, LabelRowHeight)); // over the tick labels under it
		dc.DrawText(playhead, new Point(tx, 0));

		if (IsKeyboardFocused)
		{
			dc.DrawRectangle(null, PenFor(AccentBrush), new Rect(0.5, 0.5, w - 1, h - 1));
		}
	}

	/// <summary>
	/// A bracket on the frame's pixel <paramref name="column"/> (its left edge): a 2 px bar over that column and its
	/// neighbour inside the range, with 6 px feet 2 px thick pointing into the range, filling <paramref name="inner"/>'s height.
	/// </summary>
	private void DrawBracket(DrawingContext dc, double column, Rect inner, bool open)
	{
		double left = open ? column : column - 1.0;
		double feet = open ? left : left - 4.0;
		dc.DrawRectangle(AccentBrush, null, new Rect(left, inner.Top, 2.0, inner.Height));
		dc.DrawRectangle(AccentBrush, null, new Rect(feet, inner.Top, 6.0, 2.0));
		dc.DrawRectangle(AccentBrush, null, new Rect(feet, inner.Bottom - 2.0, 6.0, 2.0));
	}

	// the labels and pens are cached (2026-10-16): built afresh, a draw of the bar cost 3 ms, mostly FormattedText,
	// and the bar is drawn on every frame-count and position change; the cache is cleared when it grows large
	// (the playhead's label changes per frame) and when the culture or the DPI changes, which the key carries
	private readonly Dictionary<(string Text, bool Bold, Brush Brush, string Culture, double Dpi), FormattedText> _labels = new();
	private readonly Dictionary<Brush, Pen> _pens = new();
	private const int LabelCacheLimit = 2048;

	private double _dpi = 1;

	private FormattedText Label(string text, Brush brush, bool bold = false)
	{
		double dpi = _dpi;
		var key = (text, bold, brush, CultureInfo.CurrentUICulture.Name, dpi);
		if (!_labels.TryGetValue(key, out FormattedText? label))
		{
			if (_labels.Count >= LabelCacheLimit)
			{
				_labels.Clear();
			}
			label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
				bold ? new Typeface(LabelTypeface.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : LabelTypeface,
				11.0, brush, dpi);
			_labels[key] = label;
		}
		return label;
	}

	private DrawingGroup? _ticks;
	private (double Width, int Maximum, Brush Tick, Brush Label, string Culture, double Dpi) _ticksKey;

	/// <summary>
	/// The minor and major ticks (3 and 6 px down from the top of <paramref name="inner"/>, the bar inside its outline)
	/// with their labels as one frozen drawing, rebuilt only when its inputs change.
	/// </summary>
	private DrawingGroup TicksDrawing(double w, Rect inner, Pen pen)
	{
		var key = (w, Maximum, TickBrush, LabelBrush, CultureInfo.CurrentCulture.Name, _dpi);
		if (_ticks != null && key == _ticksKey)
		{
			return _ticks;
		}
		var group = new DrawingGroup();
		using (DrawingContext dc = group.Open())
		{
			// the labelled interval is the smallest candidate that leaves room for its text
			double pxPerFrame = TrackWidth / Maximum;
			int major = TickCandidates[^1];
			foreach (int candidate in TickCandidates)
			{
				if (candidate * pxPerFrame >= MinLabelSpacing)
				{
					major = candidate;
					break;
				}
			}
			int minor = major >= 10 ? major / 5 : major >= 2 ? 1 : 0;
			if (minor > 0)
			{
				for (int f = 0; f <= Maximum; f += minor)
				{
					if (f % major != 0)
					{
						double x = Math.Round(XOf(f)) + 0.5;
						dc.DrawLine(pen, new Point(x, inner.Top), new Point(x, inner.Top + 3));
					}
				}
			}
			for (int f = 0; f <= Maximum; f += major)
			{
				double x = Math.Round(XOf(f)) + 0.5;
				dc.DrawLine(pen, new Point(x, inner.Top), new Point(x, inner.Top + 6));
				FormattedText label = Label(f.ToString(CultureInfo.CurrentCulture), LabelBrush);
				dc.DrawText(label, new Point(Math.Min(x + 2, w - label.Width - 1), 0));
			}
		}
		group.Freeze();
		_ticks = group;
		_ticksKey = key;
		return group;
	}

	/// <summary>A frozen one-pixel pen of <paramref name="brush"/>, made once per brush (the theme brushes are shared instances).</summary>
	private Pen PenFor(Brush brush)
	{
		if (!_pens.TryGetValue(brush, out Pen? pen))
		{
			pen = new Pen(brush, 1.0);
			pen.Freeze();
			_pens[brush] = pen;
		}
		return pen;
	}

	// ---------------------------------------------------------------- mouse

	/// <inheritdoc/>
	protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonDown(e);
		Focus();
		double x = e.GetPosition(this).X;
		_drag = BracketAt(x) switch
		{
			DragMode.RangeStart => DragMode.RangeStart,
			DragMode.RangeEnd => DragMode.RangeEnd,
			_ => DragMode.Scrub,
		};
		ApplyDrag(x);
		CaptureMouse();
		e.Handled = true;
	}

	/// <inheritdoc/>
	protected override void OnMouseMove(MouseEventArgs e)
	{
		base.OnMouseMove(e);
		double x = e.GetPosition(this).X;
		if (_drag != DragMode.None)
		{
			ApplyDrag(x);
			return;
		}
		_hoverFrame = FrameAt(x);
		Cursor = BracketAt(x) == DragMode.None ? Cursors.Arrow : Cursors.SizeWE;
		InvalidateVisual();
	}

	/// <inheritdoc/>
	protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		base.OnMouseLeftButtonUp(e);
		if (_drag != DragMode.None)
		{
			_drag = DragMode.None;
			ReleaseMouseCapture();
			InvalidateVisual();
			e.Handled = true;
		}
	}

	/// <inheritdoc/>
	protected override void OnMouseLeave(MouseEventArgs e)
	{
		base.OnMouseLeave(e);
		_hoverFrame = null;
		InvalidateVisual();
	}

	/// <inheritdoc/>
	protected override void OnMouseWheel(MouseWheelEventArgs e)
	{
		base.OnMouseWheel(e);
		Value += e.Delta > 0 ? 1 : -1;
		e.Handled = true;
	}

	private void ApplyDrag(double x)
	{
		int frame = FrameAt(x);
		switch (_drag)
		{
			case DragMode.Scrub:
				Value = frame;
				break;
			case DragMode.RangeStart:
				Execute(SetRangeStartCommand, frame);
				break;
			case DragMode.RangeEnd:
				Execute(SetRangeEndCommand, frame);
				break;
		}
	}

	private void Execute(ICommand? command, int frame)
	{
		if (command is RoutedCommand routed)
		{
			if (routed.CanExecute(frame, this))
			{
				routed.Execute(frame, this);
			}
		}
		else if (command?.CanExecute(frame) == true)
		{
			command.Execute(frame);
		}
	}

	// ---------------------------------------------------------------- keyboard

	/// <inheritdoc/>
	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		int? target = e.Key switch
		{
			Key.Left => Value - 1,
			Key.Right => Value + 1,
			Key.PageDown => Value - 10,
			Key.PageUp => Value + 10,
			Key.Home => RangeStart,
			Key.End => RangeEnd,
			_ => null,
		};
		if (target is int frame)
		{
			Value = frame;
			e.Handled = true;
		}
	}

	/// <inheritdoc/>
	protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
	{
		base.OnGotKeyboardFocus(e);
		InvalidateVisual();
	}

	/// <inheritdoc/>
	protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
	{
		base.OnLostKeyboardFocus(e);
		InvalidateVisual();
	}
}

/// <summary>Exposes a <see cref="TimelineBar"/> to UI Automation as a slider with a range value.</summary>
public sealed class TimelineBarAutomationPeer : FrameworkElementAutomationPeer, IRangeValueProvider
{
	public TimelineBarAutomationPeer(TimelineBar owner) : base(owner)
	{
	}

	private TimelineBar Bar => (TimelineBar)Owner;

	/// <inheritdoc/>
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

	/// <inheritdoc/>
	protected override string GetClassNameCore() => nameof(TimelineBar);

	/// <summary>
	/// The in and out points in frames as "range &lt;in&gt;..&lt;out&gt;", for the harnesses (the range value carries the
	/// playhead and the maximum).
	/// </summary>
	protected override string GetItemStatusCore() => $"range {Bar.RangeStart}..{Bar.RangeEnd}";

	/// <inheritdoc/>
	protected override bool IsContentElementCore() => true;

	/// <inheritdoc/>
	protected override bool IsControlElementCore() => true;

	/// <inheritdoc/>
	public override object? GetPattern(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

	/// <inheritdoc/>
	public bool IsReadOnly => false;

	/// <inheritdoc/>
	public double Value => Bar.Value;

	/// <inheritdoc/>
	public double Minimum => 0;

	/// <inheritdoc/>
	public double Maximum => Bar.Maximum;

	/// <inheritdoc/>
	public double SmallChange => 1;

	/// <inheritdoc/>
	public double LargeChange => 10;

	/// <inheritdoc/>
	public void SetValue(double value) => Bar.Value = (int)Math.Round(value);
}
