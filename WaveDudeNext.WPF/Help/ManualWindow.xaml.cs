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
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Navigation;
using System.Windows.Threading;
using NLog;
using WaveDudeNext.WPF.Theming;

namespace WaveDudeNext.WPF.Help;

/// <summary>
/// The manual window (Help > Manual, F1): the embedded MANUAL.md (<see cref="ManualDocument"/>) beside a contents tree
/// of its chapters and their sections, every branch open; the title, the one level-1 heading, is no row. The window
/// opens with the text at its top and the first chapter's row taken; taking a row goes to its heading, which then
/// stands at the top of the text; a link to "#anchor" goes to that heading, a web link opens in the browser. The text
/// scrolls by whole pixels, so a screenshot that fits the column stays drawn 1:1. Not modal, but owned by the main window
/// (the user's choice): minimised and restored with it, kept above it, opened once and closed with it. A change of the UI
/// zoom that changes the text formatting mode loads the document again (<see cref="ReloadDocument"/>). UI thread only.
/// </summary>
public partial class ManualWindow : Window
{
	private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
	/// <summary>Every heading of the document as a tree, the title included: what a link to "#anchor" is looked up in.</summary>
	private readonly IReadOnlyList<ManualHeading> _all;
	/// <summary>The viewer's scroll viewer, from its template once the window is loaded.</summary>
	private ScrollViewer? _scroller;
	/// <summary>Set while the first row is taken on opening, so the text stays at its top instead of going to the heading.</summary>
	private bool _opening;
	/// <summary>The text formatting mode the shown document was formatted with: the window content's at its first layout.</summary>
	private TextFormattingMode _formattedMode;

	/// <summary>Loads the manual and its headings; the window opens with the text at its top and the first row taken, the tree focused.</summary>
	public ManualWindow()
	{
		long start = Stopwatch.GetTimestamp();
		Document = ManualDocument.Load();
		double read = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
		_all = ManualDocument.Contents(Document);
		// the chapters are the rows: the title, the document's one level-1 heading, would be a lone root that indents
		// every other row, and the title bar names the manual already
		Headings = _all is [{ Level: 1 } title] ? title.Children : _all;
		InitializeComponent();
		SourceInitialized += (_, _) => ThemeController.Instance.ApplyTitleBar(this);
		// the links inside the loaded document bubble their navigation up to the viewer
		documentView.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnNavigate));
		Loaded += OnLoaded;
		ContentRendered += (_, _) => Logger.Debug("Manual open: {Headings} headings, the document read in {Read:0} ms, on screen after {Shown:0} ms",
			Count(_all), read, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
		Settings.Default.PropertyChanged += OnSettingChanged;
	}

	/// <summary>Identifies the <see cref="Document"/> property.</summary>
	public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
		nameof(Document), typeof(FlowDocument), typeof(ManualWindow));

	/// <summary>The manual: a new copy after a change of the UI zoom that changes the text formatting mode (<see cref="ReloadDocument"/>).</summary>
	public FlowDocument Document
	{
		get => (FlowDocument)GetValue(DocumentProperty);
		private set => SetValue(DocumentProperty, value);
	}

	/// <summary>Lets go of the settings' event, which outlives the window.</summary>
	protected override void OnClosed(EventArgs e)
	{
		Settings.Default.PropertyChanged -= OnSettingChanged;
		base.OnClosed(e);
	}

	/// <summary>
	/// A change of the UI zoom that changes the content's text formatting mode (the theme's Display at 1, Ideal when
	/// scaled) loads the document again (2026-11-04, the user's report: the text had blurred at a live change and came
	/// back sharp reopened): WPF reads the mode once, at a document's first formatting, into the text formatter it keeps
	/// for the document's life, so the lines were formatted again with the Display-mode one and drawn scaled, soft and
	/// wrapped as at 100 %.
	/// </summary>
	private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(Settings.UiZoom) && IsLoaded && WindowZoom.TextFormattingAt(this, Settings.Default.UiZoom) != _formattedMode)
		{
			ReloadDocument();
		}
	}

	/// <summary>
	/// A new copy of the document in the viewer, the headings pointed at its paragraphs, the text at the same share of
	/// its height. The old text stays on screen, scaled, until the new copy is laid out: about 180 ms to read it and the
	/// layout of the whole document after that, half a second in all, which the user sees as the text sharpening a
	/// moment after the zoom changed (2026-11-04). The log line names both times.
	/// </summary>
	private void ReloadDocument()
	{
		long start = Stopwatch.GetTimestamp();
		double zoom = Settings.Default.UiZoom;
		double share = _scroller is { ScrollableHeight: > 0 } scroller ? scroller.VerticalOffset / scroller.ScrollableHeight : 0;
		FlowDocument fresh = ManualDocument.Load();
		Relink(_all, ManualDocument.Contents(fresh));
		double read = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
		Document = fresh;
		_formattedMode = WindowZoom.TextFormattingAt(this, zoom);
		// after the new document's layout (at Render priority): its lines wrap differently, so the place is kept as a share
		Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
		{
			if (share > 0 && _scroller != null)
			{
				_scroller.ScrollToVerticalOffset(WholePixels(share * _scroller.ScrollableHeight));
			}
			Logger.Debug("Manual: the document loaded again, formatted {Mode} at UI zoom {Zoom}: read in {Read:0} ms, laid out after {Shown:0} ms",
				_formattedMode, zoom, read, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
		});
	}

	/// <summary>The headings pointed at the paragraphs of a new copy of the same document, so the tree's rows stay.</summary>
	private static void Relink(IReadOnlyList<ManualHeading> headings, IReadOnlyList<ManualHeading> fresh)
	{
		if (headings.Count != fresh.Count)
		{
			Logger.Warn("Manual: {Count} headings in the document loaded again, {Known} before; the contents tree goes nowhere until the manual is opened again", fresh.Count, headings.Count);
			return;
		}
		for (int i = 0; i < headings.Count; i++)
		{
			headings[i].Paragraph = fresh[i].Paragraph;
			Relink(headings[i].Children, fresh[i].Children);
		}
	}

	/// <summary>The contents tree's rows: the manual's chapters, each with its sections under it.</summary>
	public IReadOnlyList<ManualHeading> Headings { get; }

	/// <summary>Esc closes even while the text has the focus.</summary>
	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			e.Handled = true;
			Close();
			return;
		}
		base.OnPreviewKeyDown(e);
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		_formattedMode = WindowZoom.TextFormattingAt(this, Settings.Default.UiZoom);
		_scroller = documentView.Template.FindName("PART_ContentHost", documentView) as ScrollViewer;
		if (_scroller != null)
		{
			_scroller.ScrollChanged += OnScrollChanged;
		}
		if (contents.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first)
		{
			// taken without going there: the text opens at its top, the title in view above the first chapter
			_opening = true;
			first.IsSelected = true;
			_opening = false;
			first.Focus();
		}
	}

	private void OnContentsSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
	{
		if (!_opening && e.NewValue is ManualHeading heading)
		{
			GoTo(heading);
		}
	}

	/// <summary>Enter on a row goes to its heading again and moves the focus into the text, to read on with the keys.</summary>
	private void OnContentsKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && contents.SelectedItem is ManualHeading heading)
		{
			GoTo(heading);
			documentView.Focus();
			e.Handled = true;
		}
	}

	/// <summary>
	/// A click on the row already taken goes to its heading again (a click on another row selects it, which goes there);
	/// the innermost row takes the click, the expander's chevron is no part of it.
	/// </summary>
	private void OnContentsRowMouseUp(object sender, MouseButtonEventArgs e)
	{
		for (var element = e.OriginalSource as DependencyObject; element != null && element != sender; element = ParentOf(element))
		{
			if (element is ToggleButton or TreeViewItem)
			{
				return;
			}
		}
		if (sender is TreeViewItem { IsSelected: true, DataContext: ManualHeading heading })
		{
			GoTo(heading);
			e.Handled = true;
		}
	}

	private static DependencyObject? ParentOf(DependencyObject element) =>
		element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

	/// <summary>
	/// Scrolls the text so the heading's line stands the page's padding below the top, the end of the section before it
	/// out of sight (its own margin above would show it).
	/// </summary>
	private void GoTo(ManualHeading heading)
	{
		Rect line = heading.Paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
		if (_scroller == null || line.IsEmpty)
		{
			heading.Paragraph.BringIntoView();
			return;
		}
		double offset = _scroller.VerticalOffset + line.Top - Document.PagePadding.Top;
		_scroller.ScrollToVerticalOffset(WholePixels(Math.Max(0, offset)));
	}

	/// <summary>A dragged scroll bar leaves the text between pixels, where WPF resamples the screenshots: it goes to a whole one.</summary>
	private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (e.VerticalChange == 0 || _scroller == null)
		{
			return;
		}
		double whole = WholePixels(e.VerticalOffset);
		if (whole > _scroller.ScrollableHeight)
		{
			double scale = DevicePixels.PerDip(_scroller);
			whole = Math.Floor(_scroller.ScrollableHeight * scale) / scale;
		}
		if (Math.Abs(whole - e.VerticalOffset) > 1e-3)
		{
			_scroller.ScrollToVerticalOffset(whole);
		}
	}

	/// <summary>An offset in device-independent pixels moved to the nearest whole device pixel.</summary>
	private double WholePixels(double offset)
	{
		double scale = DevicePixels.PerDip((Visual?)_scroller ?? this);
		return Math.Round(offset * scale) / scale;
	}

	/// <summary>
	/// A link: "#anchor" goes to that heading (WPF may have resolved it against the application's pack URI), anything
	/// else opens in the browser.
	/// </summary>
	private void OnNavigate(object sender, RequestNavigateEventArgs e)
	{
		e.Handled = true;
		string? fragment = !e.Uri.IsAbsoluteUri ? e.Uri.OriginalString : e.Uri.Scheme == "pack" ? e.Uri.Fragment : null;
		if (fragment != null)
		{
			if (fragment.StartsWith('#') && Find(_all, fragment[1..]) is { } heading)
			{
				GoTo(heading);
			}
			else
			{
				Logger.Debug("Manual: no heading for the link {Uri}", e.Uri);
			}
			return;
		}
		Logger.Debug("Manual: opening {Uri}", e.Uri);
		try
		{
			Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Manual: could not open {Uri}", e.Uri);
		}
	}

	private static ManualHeading? Find(IEnumerable<ManualHeading> headings, string anchor)
	{
		foreach (ManualHeading heading in headings)
		{
			if (heading.Anchor == anchor)
			{
				return heading;
			}
			if (Find(heading.Children, anchor) is { } inner)
			{
				return inner;
			}
		}
		return null;
	}

	private static int Count(IEnumerable<ManualHeading> headings) => headings.Sum(heading => 1 + Count(heading.Children));
}
