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
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WaveDudeNext.WPF.Controls;

/// <summary>
/// A text box for one number with up/down buttons (the usual spinner, asked for 2026-10-12): Up/Down
/// change the value by <see cref="Increment"/>, PageUp/PageDown by ten increments, the mouse wheel
/// does the same while the box has keyboard focus, and the two repeat buttons on the right do it with
/// the mouse, and a horizontal drag on the text itself scrubs the value, Blender style (the user's request,
/// 2026-10-18): press, move sideways past the drag threshold and the value follows the mouse by
/// <see cref="DragStep"/> per pixel (by default one increment per <see cref="PixelsPerStep"/> pixels), ten times
/// finer with Shift, ten times coarser with Ctrl, rounded to <see cref="Decimals"/> places (by default the
/// increment's, so a whole-number box never writes a fraction); a drag that reaches the edge of the monitor wraps
/// the cursor to the opposite edge and goes on (2026-10-19, as Blender does); Escape during the drag restores the
/// value the drag started from; a press without movement stays an ordinary click that places the caret. A
/// <see cref="Logarithmic"/> box, for a gain such as a brightness, steps by a factor instead (2026-10-26). Every step
/// clamps to <see cref="Minimum"/>..<see cref="Maximum"/>, writes the text back through the
/// <see cref="TextBox.Text"/> binding at once and selects it, so a source moves on the picture with each key
/// press. Typing stays plain text: the binding converts and the model clamps as before, and Enter/Escape come
/// from <see cref="EditBehavior"/> through the base text box style. Numbers are parsed and written in the current
/// culture, like the bindings. UI thread.
/// </summary>
public sealed class NumberBox : TextBox
{
	/// <summary>Identifies the <see cref="Minimum"/> property.</summary>
	public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
		nameof(Minimum), typeof(double), typeof(NumberBox), new PropertyMetadata(double.NegativeInfinity));

	/// <summary>Identifies the <see cref="Maximum"/> property.</summary>
	public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
		nameof(Maximum), typeof(double), typeof(NumberBox), new PropertyMetadata(double.PositiveInfinity));

	/// <summary>Identifies the <see cref="Increment"/> property.</summary>
	public static readonly DependencyProperty IncrementProperty = DependencyProperty.Register(
		nameof(Increment), typeof(double), typeof(NumberBox), new PropertyMetadata(1.0));

	/// <summary>Identifies the <see cref="DragStep"/> property.</summary>
	public static readonly DependencyProperty DragStepProperty = DependencyProperty.Register(
		nameof(DragStep), typeof(double), typeof(NumberBox), new PropertyMetadata(double.NaN));

	/// <summary>Identifies the <see cref="Decimals"/> property.</summary>
	public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
		nameof(Decimals), typeof(int), typeof(NumberBox), new PropertyMetadata(-1));

	/// <summary>Identifies the <see cref="Logarithmic"/> property.</summary>
	public static readonly DependencyProperty LogarithmicProperty = DependencyProperty.Register(
		nameof(Logarithmic), typeof(bool), typeof(NumberBox), new PropertyMetadata(false));

	/// <summary>Steps per PageUp/PageDown and per wheel notch with Shift.</summary>
	private const int PageSteps = 10;

	/// <summary>The factor of one step of a <see cref="Logarithmic"/> box: two percent (seven, a tenth of a doubling, was too coarse, 2026-10-26).</summary>
	private const double StepFactor = 1.02;

	/// <summary>Mouse pixels per doubling while scrubbing a <see cref="Logarithmic"/> box (before the Shift and Ctrl factors).</summary>
	private const double PixelsPerDoubling = 40.0;

	/// <summary>Mouse pixels per increment while scrubbing when <see cref="DragStep"/> is not set.</summary>
	public const double PixelsPerStep = 2.0;

	/// <summary>Pixels the cursor is set in from the far edge when a drag wraps around the monitor, so it does not wrap straight back.</summary>
	private const int WrapInset = 2;

	private Point? _pressPoint;
	private double _pressScreenX;
	private double _wrapOffset;
	private double _pressValue;
	private bool _scrubbing;

	/// <summary>Lowest value a step can reach (typed text is not limited here; the model clamps it).</summary>
	public double Minimum
	{
		get => (double)GetValue(MinimumProperty);
		set => SetValue(MinimumProperty, value);
	}

	/// <summary>Highest value a step can reach.</summary>
	public double Maximum
	{
		get => (double)GetValue(MaximumProperty);
		set => SetValue(MaximumProperty, value);
	}

	/// <summary>Amount of one Up/Down step; 1 for integers, a fraction for real-valued settings.</summary>
	public double Increment
	{
		get => (double)GetValue(IncrementProperty);
		set => SetValue(IncrementProperty, value);
	}

	/// <summary>
	/// Change of the value per pixel of a horizontal drag (in device-independent pixels), before the Shift and Ctrl
	/// factors; NaN, the default, means one <see cref="Increment"/> per <see cref="PixelsPerStep"/> pixels.
	/// </summary>
	public double DragStep
	{
		get => (double)GetValue(DragStepProperty);
		set => SetValue(DragStepProperty, value);
	}

	/// <summary>
	/// Decimal places a step or drag writes; -1, the default, means as many as <see cref="Increment"/> has, so a box
	/// bound to an integer never writes a fraction. A box bound to a real-valued property sets it to let a fine
	/// drag (Shift) reach below its increment.
	/// </summary>
	public int Decimals
	{
		get => (int)GetValue(DecimalsProperty);
		set => SetValue(DecimalsProperty, value);
	}

	/// <summary>
	/// Whether the box holds a gain and steps by a factor (2026-10-26, the user's choice after the preview's brightness,
	/// which added 1 to 0.0001 on one press and nothing visible to 30000): Up and Down by <see cref="StepFactor"/>, two
	/// percent, PageUp and PageDown and a Shift wheel notch by ten such steps, a fifth, a drag by a doubling per
	/// <see cref="PixelsPerDoubling"/> pixels; the result rounded to three significant digits. <see cref="Increment"/>,
	/// <see cref="DragStep"/> and <see cref="Decimals"/> play no part; <see cref="Minimum"/> must be positive, where a
	/// value that is not starts.
	/// </summary>
	public bool Logarithmic
	{
		get => (bool)GetValue(LogarithmicProperty);
		set => SetValue(LogarithmicProperty, value);
	}

	/// <summary>The value the text holds, or null while it does not parse.</summary>
	public double? Value => double.TryParse(Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ? value : null;

	/// <summary>
	/// Adds <paramref name="steps"/> increments to the value (a non-parsing text counts as 0), clamps,
	/// writes the text and pushes it into the binding. Rounded to the increment's precision so that
	/// 0.1 steps do not accumulate binary noise. A <see cref="Logarithmic"/> box multiplies by <see cref="StepFactor"/> per step instead.
	/// </summary>
	public void Step(int steps)
	{
		if (IsReadOnly || !IsEnabled)
		{
			return;
		}
		Write(Logarithmic ? Positive(Value) * Math.Pow(StepFactor, steps) : (Value ?? 0.0) + steps * Increment);
	}

	/// <summary>A value a logarithmic step can start from: the one given when above zero, else the minimum, else 1.</summary>
	private double Positive(double? value) => value is > 0.0 ? value.Value : Minimum > 0.0 ? Minimum : 1.0;

	/// <summary>Clamps <paramref name="value"/>, rounds it to the box's precision, writes the text, pushes it into the binding and selects it.</summary>
	private void Write(double value)
	{
		value = Math.Clamp(value, Minimum, Maximum);
		if (Logarithmic)
		{
			// three significant digits, through the text so the double is the one that prints as them
			value = double.Parse(value.ToString("G3", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
		}
		else
		{
			value = Math.Round(value, Math.Clamp(Decimals >= 0 ? Decimals : DecimalsOf(Increment), 0, 10));
		}
		Text = value.ToString(CultureInfo.CurrentCulture);
		GetBindingExpression(TextProperty)?.UpdateSource();
		SelectAll();
	}

	/// <summary>How many decimals the increment needs ("0.01" → 2); 0 for whole numbers.</summary>
	private static int DecimalsOf(double increment)
	{
		int decimals = 0;
		while (decimals < 10 && Math.Abs(increment * Math.Pow(10, decimals) - Math.Round(increment * Math.Pow(10, decimals))) > 1e-9)
		{
			decimals++;
		}
		return decimals;
	}

	/// <inheritdoc/>
	public override void OnApplyTemplate()
	{
		base.OnApplyTemplate();
		if (GetTemplateChild("PART_Up") is RepeatButton up)
		{
			up.Click += (_, _) => StepFromButton(1);
		}
		if (GetTemplateChild("PART_Down") is RepeatButton down)
		{
			down.Click += (_, _) => StepFromButton(-1);
		}
	}

	/// <summary>A button step also takes the focus, so the written value is visibly the box's and the wheel works afterwards.</summary>
	private void StepFromButton(int direction)
	{
		if (!IsKeyboardFocusWithin)
		{
			Focus();
		}
		Step(direction);
	}

	// ---------------------------------------------------------------- scrubbing: a horizontal drag on the text changes the value

	/// <inheritdoc/>
	protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
	{
		base.OnPreviewMouseLeftButtonDown(e);
		_pressPoint = null;
		// a press on the repeat buttons is theirs; one on the text of a box that already has the focus selects text
		// as in any text box (Blender's rule, 2026-10-19: a drag scrubbed where a selection was meant); one on an
		// unfocused box may become a scrub, or stay a click that focuses it and places the caret
		if (e.Handled || IsReadOnly || !IsEnabled || IsOnRepeatButton(e.OriginalSource) || IsKeyboardFocusWithin)
		{
			return;
		}
		_pressPoint = e.GetPosition(this);
		_pressScreenX = PointToScreen(_pressPoint.Value).X;
		_wrapOffset = 0.0;
		_pressValue = Value ?? 0.0;
	}

	/// <inheritdoc/>
	protected override void OnPreviewMouseMove(MouseEventArgs e)
	{
		base.OnPreviewMouseMove(e);
		// the text box captured the mouse on the press, so a release always reaches this box or ends the capture;
		// the button state is read as well in case neither happened
		if (_pressPoint == null || (e.LeftButton != MouseButtonState.Pressed && !IsMouseCaptureWithin))
		{
			return;
		}
		// measured on the screen (device pixels) so a wrap of the cursor can be taken out again; the box's own
		// coordinates would also shift if the drag scrolled or re-laid out the form
		Point screen = PointToScreen(e.GetPosition(this));
		double scale = DevicePixels.PerDip(this);
		double dx = (screen.X + _wrapOffset - _pressScreenX) / scale;
		if (!_scrubbing)
		{
			if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance)
			{
				return;
			}
			_scrubbing = true;
			CaptureMouse();
			Cursor = Cursors.SizeWE;
			if (!IsKeyboardFocusWithin)
			{
				Focus();
			}
		}
		double step = Logarithmic ? 1.0 / PixelsPerDoubling : double.IsNaN(DragStep) ? Increment / PixelsPerStep : DragStep;
		if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
		{
			step /= 10.0;
		}
		else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
		{
			step *= 10.0;
		}
		Write(Logarithmic ? Positive(_pressValue) * Math.Pow(2.0, dx * step) : _pressValue + dx * step);
		WrapAtMonitorEdge(screen);
		e.Handled = true;   // not a text selection
	}

	/// <summary>
	/// A drag that reaches the left or right edge of the monitor under the cursor jumps the cursor to the opposite
	/// edge, remembering the jump in <see cref="_wrapOffset"/> so the drag distance runs on unbroken; otherwise a
	/// value could only be dragged as far as the screen allowed.
	/// </summary>
	private void WrapAtMonitorEdge(Point screen)
	{
		var at = new NativePoint { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
		IntPtr monitor = MonitorFromPoint(at, MonitorDefaultToNearest);
		var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
		{
			return;
		}
		int left = info.Monitor.Left, right = info.Monitor.Right;   // right is exclusive
		if (right - left <= 2 * WrapInset + 1)
		{
			return;
		}
		int target;
		if (at.X <= left)
		{
			target = right - 1 - WrapInset;
		}
		else if (at.X >= right - 1)
		{
			target = left + WrapInset;
		}
		else
		{
			return;
		}
		if (SetCursorPos(target, at.Y))
		{
			_wrapOffset += at.X - target;
		}
	}

	private const uint MonitorDefaultToNearest = 2;

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MonitorInfo
	{
		public int Size;
		public NativeRect Monitor;
		public NativeRect Work;
		public uint Flags;
	}

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

	[DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	/// <inheritdoc/>
	protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
	{
		base.OnPreviewMouseLeftButtonUp(e);
		if (_scrubbing)
		{
			EndScrub();
			e.Handled = true;
		}
		_pressPoint = null;
	}

	/// <inheritdoc/>
	protected override void OnLostMouseCapture(MouseEventArgs e)
	{
		base.OnLostMouseCapture(e);
		if (_scrubbing)
		{
			EndScrub();
		}
		_pressPoint = null;
	}

	private void EndScrub()
	{
		_scrubbing = false;
		_pressPoint = null;
		ClearValue(CursorProperty);
		if (IsMouseCaptured)
		{
			ReleaseMouseCapture();
		}
	}

	private static bool IsOnRepeatButton(object source)
	{
		for (DependencyObject? d = source as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : null)
		{
			if (d is RepeatButton)
			{
				return true;
			}
			if (d is NumberBox)
			{
				return false;
			}
		}
		return false;
	}

	/// <inheritdoc/>
	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		base.OnPreviewKeyDown(e);
		if (e.Handled)
		{
			return;
		}
		if (_scrubbing && e.Key == Key.Escape)
		{
			// the drag is abandoned: back to the value it started from
			Write(_pressValue);
			EndScrub();
			e.Handled = true;
			return;
		}
		int steps = e.Key switch
		{
			Key.Up => 1,
			Key.Down => -1,
			Key.PageUp => PageSteps,
			Key.PageDown => -PageSteps,
			_ => 0,
		};
		if (steps != 0)
		{
			Step(steps);
			e.Handled = true;
		}
	}

	/// <inheritdoc/>
	protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
	{
		base.OnPreviewMouseWheel(e);
		if (e.Handled || !IsKeyboardFocusWithin || e.Delta == 0)
		{
			return;
		}
		int steps = Math.Sign(e.Delta) * ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? PageSteps : 1);
		Step(steps);
		e.Handled = true;
	}
}
