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

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using NLog;

namespace WaveDudeNext.WPF.Theming;

/// <summary>
/// The UI zoom (View > UI zoom, <see cref="Settings.UiZoom"/>; 2026-11-02, the user's request) on every window of the
/// app, applied live. The theme's Window style sets <see cref="EnabledProperty"/>; once a window is initialised (its
/// XAML read, the content in place, nothing shown yet), and again whenever the setting changes, the window's content
/// gets a layout scale by the zoom and Ideal text formatting (the theme's Display formatting rasterises glyphs on the
/// 100 % grid, and WPF scales those bitmaps through a transform, soft; 2026-11-01, seen in a capture), and the window's
/// explicit sizes, Width, Height and the minimum and maximum, each when set and finite, are multiplied by the change
/// of zoom (a bound size, the main window's, carries the zoom from <see cref="Converters.ZoomConverter"/> when the
/// XAML is read and is set like the others after a change); a dimension sized to content follows the content. The
/// window is then kept inside its monitor's work area (<see cref="KeepOnScreen"/>, the user's rule), shrunk to it when
/// larger.
/// <para>
/// Whole pixels at any factor (the user's rule: a line is there or not, 1 or 2 pixels wide, never cut): WPF rounds
/// its layout to the DPI it believes its tree has and knows nothing of a transform, so a window's tree is told the
/// monitor's DPI times the zoom (<see cref="VisualTreeHelper.SetRootDpi"/>, meant for this; a system-DPI-aware process
/// never resets it), which puts every rounded size and offset under the transform on a device pixel, while the window
/// itself and its chrome, outside the transform, round to nothing and keep the client area's whole real pixels.
/// </para>
/// <para>
/// The process is DPI aware per monitor (the manifest, 2026-11-02, the user's choice): WPF then gives every new visual
/// its parent's DPI (its AddVisualChild does so in that mode alone), puts the monitor's DPI on a tree when a window's
/// handle takes it as its root, and again when the window lands on a monitor of another scale; the zoomed DPI is put
/// back after each (the window's source change, its DpiChanged event). A popup is a tree of its own, WPF's PopupRoot,
/// given a window on the monitor's DPI at each opening, and it must be told the zoomed DPI before its first drawing:
/// a Border builds the pens of its edges once, at the first drawing, for the DPI it sees then (the user's report: a
/// tooltip's edge 1.5 px wide, a half-transparent line beside the fill). So the popup's content is watched for the
/// source it gets at each opening, which comes before any layout or drawing: the theme's own popups (menus,
/// drop-downs, the colour button) through <see cref="PopupProperty"/> on the popup, a tooltip or context menu, the
/// content of a parentless popup of WPF's own that takes no transform from its target (Popup.UpdateTransform in WPF's
/// source), through <see cref="StandaloneProperty"/> in its style, whose other setters give it the scale and the
/// formatting bound to the setting.
/// </para>
/// At zoom 1 everything is as the theme has it. UI thread.
/// </summary>
public static class WindowZoom
{
	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

	/// <summary>Whether the window takes the UI zoom; set by the theme's Window style.</summary>
	public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
		"Enabled", typeof(bool), typeof(WindowZoom), new PropertyMetadata(false, OnEnabledChanged));

	/// <summary>Gets <see cref="EnabledProperty"/>.</summary>
	public static bool GetEnabled(Window window) => (bool)window.GetValue(EnabledProperty);

	/// <summary>Sets <see cref="EnabledProperty"/>.</summary>
	public static void SetEnabled(Window window, bool value) => window.SetValue(EnabledProperty, value);

	/// <summary>Whether a popup of the theme's templates has its tree zoomed at each opening; set on the popup in the template.</summary>
	public static readonly DependencyProperty PopupProperty = DependencyProperty.RegisterAttached(
		"Popup", typeof(bool), typeof(WindowZoom), new PropertyMetadata(false, OnPopupChanged));

	/// <summary>Gets <see cref="PopupProperty"/>.</summary>
	public static bool GetPopup(Popup popup) => (bool)popup.GetValue(PopupProperty);

	/// <summary>Sets <see cref="PopupProperty"/>.</summary>
	public static void SetPopup(Popup popup, bool value) => popup.SetValue(PopupProperty, value);

	/// <summary>Whether an element that is itself the content of a popup of WPF's own (a tooltip, a context menu) has its tree zoomed at each opening; set by the theme's styles.</summary>
	public static readonly DependencyProperty StandaloneProperty = DependencyProperty.RegisterAttached(
		"Standalone", typeof(bool), typeof(WindowZoom), new PropertyMetadata(false, OnStandaloneChanged));

	/// <summary>Gets <see cref="StandaloneProperty"/>.</summary>
	public static bool GetStandalone(FrameworkElement element) => (bool)element.GetValue(StandaloneProperty);

	/// <summary>Sets <see cref="StandaloneProperty"/>.</summary>
	public static void SetStandalone(FrameworkElement element, bool value) => element.SetValue(StandaloneProperty, value);

	/// <summary>The zoom a window's explicit sizes were last multiplied by, NaN before the first time: a change multiplies by the ratio.</summary>
	private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached(
		"Applied", typeof(double), typeof(WindowZoom), new PropertyMetadata(double.NaN));

	/// <summary>The factor a tree's root was last told its DPI is multiplied by, 1 to begin with: the real DPI is read back through it while the tree has no window.</summary>
	private static readonly DependencyProperty RootZoomProperty = DependencyProperty.RegisterAttached(
		"RootZoom", typeof(double), typeof(WindowZoom), new PropertyMetadata(1.0));

	/// <summary>Whether a popup's content is watched for its source; once per content.</summary>
	private static readonly DependencyProperty WatchedProperty = DependencyProperty.RegisterAttached(
		"Watched", typeof(bool), typeof(WindowZoom), new PropertyMetadata(false));

	// the bounds before the sizes: a shrinking minimum or a growing maximum must be in place when the size moves
	private static readonly DependencyProperty[] Sizes =
	[
		FrameworkElement.MinWidthProperty, FrameworkElement.MinHeightProperty, FrameworkElement.MaxWidthProperty,
		FrameworkElement.MaxHeightProperty, FrameworkElement.WidthProperty, FrameworkElement.HeightProperty,
	];

	/// <summary>The zoom in force: the setting.</summary>
	public static double Current => Settings.Default.UiZoom;

	/// <summary>Follows the setting for every open window; once per process, before the first window.</summary>
	public static void Install() => Settings.Default.PropertyChanged += OnSettingChanged;

	private static void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(Settings.UiZoom))
		{
			return;
		}
		foreach (Window window in Application.Current.Windows)
		{
			if (GetEnabled(window) && window.IsInitialized)
			{
				Apply(window);
			}
		}
	}

	// ---------------------------------------------------------------- windows

	private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not Window window || !(bool)e.NewValue)
		{
			return;
		}
		// the style is applied while the window's XAML is still being read; the content is there at Initialized, and
		// the window's handle, placed, at SourceInitialized, before anything shows
		if (window.IsInitialized)
		{
			Apply(window);
		}
		else
		{
			window.Initialized += (sender, _) => Apply((Window)sender!);
		}
		// WPF puts the monitor's DPI on the tree when the window's handle takes it as its root and when the window
		// lands on a monitor of another scale: the zoomed DPI put back after each. Telling the tree its DPI raises the
		// window's DpiChanged too, so that handler goes by the real DPI of the handle, unchanged then, never by the
		// event's value, which would be the zoomed one and feed back without end (2026-11-02, a silent crash)
		PresentationSource.AddSourceChangedHandler(window, OnWindowSourceChanged);
		window.DpiChanged += (sender, _) => ZoomRoot((Window)sender!, Current);
		window.SourceInitialized += (sender, _) => KeepOnScreen((Window)sender!);
	}

	private static void OnWindowSourceChanged(object sender, SourceChangedEventArgs e)
	{
		if (sender is Window window && e.NewSource != null)
		{
			ZoomRoot(window, Current);
		}
	}

	private static void Apply(Window window)
	{
		double zoom = Current;
		double applied = (double)window.GetValue(AppliedProperty);
		// the first time, a bound size is at the zoom already: its converter read the setting when the XAML was read
		bool first = double.IsNaN(applied);
		if (first)
		{
			applied = 1;
		}
		ZoomRoot(window, zoom);
		if (window.Content is FrameworkElement content)
		{
			ZoomContent(content, zoom, true);
		}
		window.SetValue(AppliedProperty, zoom);
		if (zoom == applied)
		{
			return;
		}
		// each size set to its value at the new zoom, a bound one too: the two-way binding keeps and takes the value
		// back through ZoomConverter, which divides by the new zoom. A bound size must not be re-read from its source
		// instead: resizing the window's handle for the first size makes WPF write both sizes back from the handle,
		// and the other one, still at the old zoom, then lands in the settings divided by the new (2026-11-02, the
		// main window kept its height at 1.5 and its saved height had shrunk); set explicitly, the second size puts
		// the right value back
		foreach (DependencyProperty size in Sizes)
		{
			if (first && BindingOperations.IsDataBound(window, size))
			{
				continue;
			}
			double value = (double)window.GetValue(size);
			if (value > 0 && double.IsFinite(value))
			{
				window.SetValue(size, value * zoom / applied);
			}
		}
		KeepOnScreen(window);
	}

	/// <summary>
	/// The text formatting mode a window's content has at a zoom, what <see cref="ZoomContent"/> gives it: Ideal when
	/// scaled, else the window's own (the theme's Display). A flow document must be loaded again when it changes
	/// (<see cref="Help.ManualWindow"/>): WPF reads the mode once, at the document's first formatting, into the text
	/// formatter it keeps for the document's life, so a change re-formats the lines with the old one.
	/// </summary>
	public static TextFormattingMode TextFormattingAt(Window window, double zoom) =>
		zoom == 1 ? TextOptions.GetTextFormattingMode(window) : TextFormattingMode.Ideal;

	// ---------------------------------------------------------------- popups

	private static void OnPopupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is not Popup popup || !(bool)e.NewValue)
		{
			return;
		}
		// the template sets the property before the popup's content: watched once the popup has loaded with its window
		if (popup.Child is FrameworkElement child)
		{
			Watch(child);
		}
		popup.Loaded += (sender, _) =>
		{
			if (((Popup)sender!).Child is FrameworkElement loaded)
			{
				Watch(loaded);
			}
		};
	}

	private static void OnStandaloneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		if (d is FrameworkElement element && (bool)e.NewValue)
		{
			Watch(element);
		}
	}

	/// <summary>A popup's content watched for the source it gets at each opening, once.</summary>
	private static void Watch(FrameworkElement content)
	{
		if ((bool)content.GetValue(WatchedProperty))
		{
			return;
		}
		content.SetValue(WatchedProperty, true);
		PresentationSource.AddSourceChangedHandler(content, OnPopupContentSourceChanged);
	}

	// the popup's window exists, nothing is laid out or drawn yet: the tree's root (WPF's PopupRoot, with the placement
	// target's transform for a popup inside a window's tree) told the zoomed DPI and rounding to nothing, the content rounding
	private static void OnPopupContentSourceChanged(object sender, SourceChangedEventArgs e)
	{
		if (sender is not FrameworkElement content || e.NewSource?.RootVisual is not FrameworkElement root || ReferenceEquals(root, content))
		{
			return;
		}
		double zoom = Current;
		ZoomRoot(root, zoom);
		ZoomContent(content, zoom, false);
		// a popup opened before a change of the zoom keeps its content's layout from then: WPF puts the new transform
		// on the popup but reuses each entry's measure under the unchanged constraint, and an entry moves onto the new
		// grid only when something invalidates it, a hover (2026-11-02, the user's report: menu entries shifting by a
		// pixel on hover after a switch); so the whole tree is measured, arranged and drawn afresh at each opening
		InvalidateTree(root);
		Logger.Trace("UI zoom {Zoom} on a popup's tree ({Content})", zoom, content.GetType().Name);
	}

	/// <summary>Every element of a tree measured, arranged and drawn again.</summary>
	private static void InvalidateTree(DependencyObject visual)
	{
		if (visual is UIElement element)
		{
			element.InvalidateMeasure();
			element.InvalidateArrange();
			element.InvalidateVisual();
		}
		int count = VisualTreeHelper.GetChildrenCount(visual);
		for (int i = 0; i < count; i++)
		{
			InvalidateTree(VisualTreeHelper.GetChild(visual, i));
		}
	}

	// ---------------------------------------------------------------- trees

	/// <summary>The content under a zoomed root: rounding on the zoomed grid, and with <paramref name="scaled"/> the scale and the Ideal formatting themselves.</summary>
	/// <summary>
	/// How text is rasterised in every window and popup: Auto, which is ClearType on a screen, unless the start-up switch
	/// <c>--greyscale-text</c> made it Grayscale (2026-11-04, the user's rule for the manual's figures: a picture never lands
	/// on the reader's subpixel grid, so its text carries no colour fringes; the app itself keeps ClearType). Read when
	/// the styles load, so it is set before any window.
	/// </summary>
	public static TextRenderingMode TextRendering { get; set; } = TextRenderingMode.Auto;

	private static void ZoomContent(FrameworkElement content, double zoom, bool scaled)
	{
		if (scaled)
		{
			if (TextRendering == TextRenderingMode.Auto)
			{
				content.ClearValue(TextOptions.TextRenderingModeProperty);
			}
			else
			{
				TextOptions.SetTextRenderingMode(content, TextRendering);
			}
		}
		if (zoom == 1)
		{
			content.ClearValue(FrameworkElement.UseLayoutRoundingProperty);
			if (scaled)
			{
				content.ClearValue(FrameworkElement.LayoutTransformProperty);
				content.ClearValue(TextOptions.TextFormattingModeProperty);
			}
			return;
		}
		content.UseLayoutRounding = true;
		if (scaled)
		{
			if (content.LayoutTransform is not ScaleTransform { ScaleX: var x, ScaleY: var y } || x != zoom || y != zoom)
			{
				var scale = new ScaleTransform(zoom, zoom);
				scale.Freeze();
				content.LayoutTransform = scale;
			}
			TextOptions.SetTextFormattingMode(content, TextFormattingMode.Ideal);
		}
	}

	/// <summary>Set while a tree is being told its DPI, which raises DpiChanged on its way: nothing re-enters then.</summary>
	private static bool _telling;

	/// <summary>
	/// The root of a tree told the real DPI times the zoom, so the layout under the zoom's transform rounds to device
	/// pixels; the root itself rounds to nothing, since its own pixels are the real ones. At zoom 1 the real DPI and the
	/// theme's rounding again.
	/// </summary>
	private static void ZoomRoot(FrameworkElement root, double zoom)
	{
		if (_telling)
		{
			return;
		}
		DpiScale real = RealDpi(root);
		var target = new DpiScale(real.DpiScaleX * zoom, real.DpiScaleY * zoom);
		DpiScale current = VisualTreeHelper.GetDpi(root);
		if (Math.Abs(current.DpiScaleX - target.DpiScaleX) > 1e-9 || Math.Abs(current.DpiScaleY - target.DpiScaleY) > 1e-9)
		{
			_telling = true;
			try
			{
				VisualTreeHelper.SetRootDpi(root, target);
			}
			finally
			{
				_telling = false;
			}
		}
		root.SetValue(RootZoomProperty, zoom);
		if (zoom == 1)
		{
			root.ClearValue(FrameworkElement.UseLayoutRoundingProperty);
		}
		else
		{
			root.UseLayoutRounding = false;
		}
	}

	/// <summary>
	/// The DPI a tree's window renders at: the composition target's transform to the device, which no zoom touches;
	/// before the tree has a window, its own DPI divided by what it was last told.
	/// </summary>
	private static DpiScale RealDpi(FrameworkElement root)
	{
		if (PresentationSource.FromVisual(root)?.CompositionTarget is { } target)
		{
			Matrix toDevice = target.TransformToDevice;
			return new DpiScale(toDevice.M11, toDevice.M22);
		}
		DpiScale told = VisualTreeHelper.GetDpi(root);
		double rootZoom = (double)root.GetValue(RootZoomProperty);
		return new DpiScale(told.DpiScaleX / rootZoom, told.DpiScaleY / rootZoom);
	}

	// ---------------------------------------------------------------- the screen

	private const uint MonitorDefaultToNearest = 2;

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect
	{
		public int Left, Top, Right, Bottom;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct MonitorInfo
	{
		public int Size;
		public NativeRect Monitor;
		public NativeRect Work;
		public uint Flags;
	}

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

	/// <summary>
	/// Keeps a window with a handle inside the work area of the monitor it is on (2026-11-02, the user's rule: a zoomed
	/// window had wandered off the screen): shrunk to the work area when larger, then moved so that it lies within. The
	/// placement goes through the window's Left, Top, Width and Height in device-independent pixels, so a bound size
	/// or position (the main window's) lands in the settings as a drag of the frame would. A maximized or minimized
	/// window is left alone.
	/// </summary>
	private static void KeepOnScreen(Window window)
	{
		IntPtr hwnd = new WindowInteropHelper(window).Handle;
		if (hwnd == IntPtr.Zero || window.WindowState != WindowState.Normal || PresentationSource.FromVisual(window)?.CompositionTarget is not { } target)
		{
			return;
		}
		var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		if (!GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info) || !GetWindowRect(hwnd, out NativeRect rect))
		{
			return;
		}
		int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
		int workWidth = info.Work.Right - info.Work.Left, workHeight = info.Work.Bottom - info.Work.Top;
		int newWidth = Math.Min(width, workWidth), newHeight = Math.Min(height, workHeight);
		int newLeft = Math.Clamp(rect.Left, info.Work.Left, info.Work.Right - newWidth);
		int newTop = Math.Clamp(rect.Top, info.Work.Top, info.Work.Bottom - newHeight);
		if (newWidth == width && newHeight == height && newLeft == rect.Left && newTop == rect.Top)
		{
			return;
		}
		Matrix toDevice = target.TransformToDevice;
		if (newWidth != width)
		{
			window.Width = newWidth / toDevice.M11;
		}
		if (newHeight != height)
		{
			window.Height = newHeight / toDevice.M22;
		}
		window.Left = newLeft / toDevice.M11;
		window.Top = newTop / toDevice.M22;
		Logger.Debug("UI zoom: the window {Window} kept on the screen, {Width}×{Height} at {Left},{Top} (work area {WorkWidth}×{WorkHeight})",
			window.GetType().Name, newWidth, newHeight, newLeft, newTop, workWidth, workHeight);
	}
}
