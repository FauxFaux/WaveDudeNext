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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WaveDudeNext.Model;
using WaveDudeNext.Rendering;
using WaveDudeNext.Runs;
using WaveDudeNext.WPF.Commands;
using WaveDudeNext.WPF.Converters;
using WaveDudeNext.WPF.Rendering;

namespace WaveDudeNext.WPF.Panes;

/// <summary>
/// The document in the centre: the field and the intensity picture, side by side or stacked,
/// with a transport bar to scrub, play and pause through the frames of a run at a chosen
/// speed. The frames come from the <see cref="Run"/>'s <see cref="FrameStore"/> through a
/// <see cref="FramePresenter"/>, which colours the frame at <see cref="Position"/> with the
/// project's display settings (<see cref="FieldSettings"/>, <see cref="IntensitySettings"/>:
/// colour scheme, brightness, black level; changing them re-colours the frame shown). Without a
/// run the pictures are placeholders. <see cref="FrameCount"/> follows the project's step count
/// and visualisation interval through a binding, and so do the <see cref="InPoint"/>/
/// <see cref="OutPoint"/> of the export range (<c>Display.ExportFrom/To</c>, in steps; the
/// <see cref="PreviewCommands"/> write them). Playback runs between the two, looping when
/// <see cref="IsLooping"/>.
/// </summary>
/// <remarks>
/// While a run is in progress the axis spans the planned frames (<see cref="PlannedLastFrame"/>)
/// and <see cref="LastFrame"/> is the last frame that exists so far; <see cref="IsLive"/> keeps
/// <see cref="Position"/> on that newest frame until the user scrubs, steps or plays, and the
/// Live button (or F) re-attaches. Playback runs on a <see cref="DispatcherTimer"/> on the UI
/// thread: a speed of 1 advances <see cref="Fps"/> (the project's video frame rate) frames per
/// second of wall-clock time, measured with a stopwatch so that a slow tick does not slow the
/// clock. Play, Pause and Space (toggle) are the built-in <see cref="MediaCommands"/>.
/// Keyboard: every transport control is a tab stop (play, pause, scrubber, speed, split, and
/// the splitter between the pictures). The split radios are one stop that lands on the checked
/// one; Left/Right there check the other. Arrows move the splitter; Enter (or a double-click)
/// resets it to the middle. Space toggles playback from anywhere in the pane except the speed
/// text box, and a click on the pictures focuses the pane so Space works after it. Executing
/// Play or Pause from the keyboard moves focus to the other button, because the one just used
/// is about to be disabled.
/// </remarks>
public partial class PreviewPane : UserControl
{

	/// <summary>Smallest accepted <see cref="Speed"/>.</summary>
	public const double MinSpeed = 0.01;

	/// <summary>Largest accepted <see cref="Speed"/>.</summary>
	public const double MaxSpeed = 100.0;

	/// <summary>Speed factors offered in the drop-down; any other value can be typed.</summary>
	public static IReadOnlyList<double> SpeedPresets { get; } = [0.25, 0.5, 1.0, 1.5, 2.0, 4.0];

	/// <summary>The picture shown while there is no run: black, in the canvas's size so the overlay and the aspect ratio are right.</summary>
	private BitmapSource _blackFrame = BlackFrame(16, 9);
	private BitmapSource? _sceneBitmap;
	private byte[]? _edges;

	/// <summary>Identifies the <see cref="Project"/> property.</summary>
	public static readonly DependencyProperty ProjectProperty = DependencyProperty.Register(
		nameof(Project), typeof(Project), typeof(PreviewPane), new PropertyMetadata(null, (d, _) => ((PreviewPane)d).OnProjectReplaced()));

	/// <summary>Identifies the <see cref="Run"/> property.</summary>
	public static readonly DependencyProperty RunProperty = DependencyProperty.Register(
		nameof(Run), typeof(SimulationRun), typeof(PreviewPane), new PropertyMetadata(null, OnRunChanged));

	/// <summary>Identifies the <see cref="FrameCount"/> property.</summary>
	public static readonly DependencyProperty FrameCountProperty = DependencyProperty.Register(
		nameof(FrameCount), typeof(int), typeof(PreviewPane),
		new PropertyMetadata(1, OnFrameCountChanged, (_, value) => Math.Max(1, (int)value)));

	private static readonly DependencyPropertyKey LastFrameKey = DependencyProperty.RegisterReadOnly(
		nameof(LastFrame), typeof(int), typeof(PreviewPane), new PropertyMetadata(0));

	/// <summary>Identifies the <see cref="LastFrame"/> property.</summary>
	public static readonly DependencyProperty LastFrameProperty = LastFrameKey.DependencyProperty;

	private static readonly DependencyPropertyKey PlannedLastFrameKey = DependencyProperty.RegisterReadOnly(
		nameof(PlannedLastFrame), typeof(int), typeof(PreviewPane), new PropertyMetadata(0));

	/// <summary>Identifies the <see cref="PlannedLastFrame"/> property.</summary>
	public static readonly DependencyProperty PlannedLastFrameProperty = PlannedLastFrameKey.DependencyProperty;

	private static readonly DependencyPropertyKey AvailableKey = DependencyProperty.RegisterReadOnly(
		nameof(Available), typeof(int), typeof(PreviewPane), new PropertyMetadata(0));

	/// <summary>Identifies the <see cref="Available"/> property.</summary>
	public static readonly DependencyProperty AvailableProperty = AvailableKey.DependencyProperty;

	/// <summary>Identifies the <see cref="Position"/> property.</summary>
	public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
		nameof(Position), typeof(int), typeof(PreviewPane),
		new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged, CoercePosition));

	/// <summary>Identifies the <see cref="IsPlaying"/> property.</summary>
	public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
		nameof(IsPlaying), typeof(bool), typeof(PreviewPane), new PropertyMetadata(false, OnIsPlayingChanged));

	/// <summary>Identifies the <see cref="IsLive"/> property.</summary>
	public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
		nameof(IsLive), typeof(bool), typeof(PreviewPane),
		new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsLiveChanged));

	/// <summary>Identifies the <see cref="Speed"/> property.</summary>
	public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(
		nameof(Speed), typeof(double), typeof(PreviewPane),
		new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, null, CoerceSpeed));

	/// <summary>Identifies the <see cref="Fps"/> property.</summary>
	public static readonly DependencyProperty FpsProperty = DependencyProperty.Register(
		nameof(Fps), typeof(int), typeof(PreviewPane), new PropertyMetadata(Video.DefaultFps, null, (_, value) => Math.Max(1, (int)value)));


	/// <summary>Identifies the <see cref="InPoint"/> property.</summary>
	public static readonly DependencyProperty InPointProperty = DependencyProperty.Register(
		nameof(InPoint), typeof(int), typeof(PreviewPane), new PropertyMetadata(0));

	/// <summary>Identifies the <see cref="OutPoint"/> property.</summary>
	public static readonly DependencyProperty OutPointProperty = DependencyProperty.Register(
		nameof(OutPoint), typeof(int), typeof(PreviewPane), new PropertyMetadata(0));

	/// <summary>Identifies the <see cref="IsLooping"/> property.</summary>
	public static readonly DependencyProperty IsLoopingProperty = DependencyProperty.Register(
		nameof(IsLooping), typeof(bool), typeof(PreviewPane), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

	/// <summary>Identifies the <see cref="Orientation"/> property.</summary>
	public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
		nameof(Orientation), typeof(Orientation), typeof(PreviewPane),
		new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

	/// <summary>Identifies the <see cref="DitherBits"/> property.</summary>
	public static readonly DependencyProperty DitherBitsProperty = DependencyProperty.Register(
		nameof(DitherBits), typeof(int), typeof(PreviewPane), new PropertyMetadata(0, (d, e) => ((PreviewPane)d)._presenter.DitherBits = (int)e.NewValue));

	/// <summary>Identifies the <see cref="FieldImage"/> property.</summary>
	public static readonly DependencyProperty FieldImageProperty = DependencyProperty.Register(
		nameof(FieldImage), typeof(ImageSource), typeof(PreviewPane));

	/// <summary>Identifies the <see cref="ScenePixels"/> property.</summary>
	public static readonly DependencyProperty ScenePixelsProperty = DependencyProperty.Register(
		nameof(ScenePixels), typeof(ScenePixels), typeof(PreviewPane), new PropertyMetadata(null, (d, _) => ((PreviewPane)d).OnScenePixelsChanged()));

	/// <summary>Identifies the <see cref="FieldOverlay"/> property.</summary>
	public static readonly DependencyProperty FieldOverlayProperty = DependencyProperty.Register(
		nameof(FieldOverlay), typeof(ImageSource), typeof(PreviewPane));

	/// <summary>Identifies the <see cref="IntensityOverlay"/> property.</summary>
	public static readonly DependencyProperty IntensityOverlayProperty = DependencyProperty.Register(
		nameof(IntensityOverlay), typeof(ImageSource), typeof(PreviewPane));

	/// <summary>Attached to each picture cell: the overlay layer its template draws over the frame.</summary>
	public static readonly DependencyProperty OverlayImageProperty = DependencyProperty.RegisterAttached(
		"OverlayImage", typeof(ImageSource), typeof(PreviewPane));

	public static ImageSource? GetOverlayImage(DependencyObject element) => (ImageSource?)element.GetValue(OverlayImageProperty);

	public static void SetOverlayImage(DependencyObject element, ImageSource? value) => element.SetValue(OverlayImageProperty, value);

	/// <summary>Attached to each picture cell: which output it shows, so its template draws the graph boxes of that layer's probes.</summary>
	public static readonly DependencyProperty LayerProperty = DependencyProperty.RegisterAttached(
		"Layer", typeof(ProbeLayer), typeof(PreviewPane), new PropertyMetadata(ProbeLayer.Field));

	public static ProbeLayer GetLayer(DependencyObject element) => (ProbeLayer)element.GetValue(LayerProperty);

	public static void SetLayer(DependencyObject element, ProbeLayer value) => element.SetValue(LayerProperty, value);

	/// <summary>Attached to each picture cell: the display settings of its layer, which the brightness box in its header edits.</summary>
	public static readonly DependencyProperty SettingsProperty = DependencyProperty.RegisterAttached(
		"Settings", typeof(DisplayLayer), typeof(PreviewPane));

	public static DisplayLayer? GetSettings(DependencyObject element) => (DisplayLayer?)element.GetValue(SettingsProperty);

	public static void SetSettings(DependencyObject element, DisplayLayer? value) => element.SetValue(SettingsProperty, value);

	/// <summary>
	/// The frames the pictures show and the index of the one shown (the scratch frame, index 0, or the run's frame at the
	/// position), or null when nothing is shown; for the brightness fit. UI thread.
	/// </summary>
	public (IFrameSource Source, int Frame)? ShownFrame => _presenter.Source is { } source ? (source, _shownFrame) : null;

	/// <summary>Index of the frame last asked of the presenter in its source (see <see cref="ShownFrame"/>).</summary>
	private int _shownFrame;

	/// <summary>Identifies the <see cref="IntensityImage"/> property.</summary>
	public static readonly DependencyProperty IntensityImageProperty = DependencyProperty.Register(
		nameof(IntensityImage), typeof(ImageSource), typeof(PreviewPane));

	/// <summary>Identifies the <see cref="SelectedItem"/> property.</summary>
	public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
		nameof(SelectedItem), typeof(object), typeof(PreviewPane), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

	/// <summary>Identifies the <see cref="SelectedItems"/> property.</summary>
	public static readonly DependencyProperty SelectedItemsProperty = DependencyProperty.Register(
		nameof(SelectedItems), typeof(ObservableCollection<object>), typeof(PreviewPane));

	/// <summary>Identifies the <see cref="IsStale"/> property.</summary>
	public static readonly DependencyProperty IsStaleProperty = DependencyProperty.Register(
		nameof(IsStale), typeof(bool), typeof(PreviewPane), new PropertyMetadata(false, (d, _) => ((PreviewPane)d).UpdateSource()));

	/// <summary>Identifies the <see cref="IsComputing"/> property.</summary>
	public static readonly DependencyProperty IsComputingProperty = DependencyProperty.Register(
		nameof(IsComputing), typeof(bool), typeof(PreviewPane), new PropertyMetadata(false, (d, _) => ((PreviewPane)d).OnComputingChanged()));

	private static readonly DependencyPropertyKey IsBusyOverlayVisibleKey = DependencyProperty.RegisterReadOnly(
		nameof(IsBusyOverlayVisible), typeof(bool), typeof(PreviewPane), new PropertyMetadata(false));

	/// <summary>Identifies the <see cref="IsBusyOverlayVisible"/> property.</summary>
	public static readonly DependencyProperty IsBusyOverlayVisibleProperty = IsBusyOverlayVisibleKey.DependencyProperty;

	/// <summary>How long a frame may take before the pictures dim and the spinner shows; shorter waits pass unmarked, so a fast machine does not strobe.</summary>
	private static readonly TimeSpan BusyOverlayDelay = TimeSpan.FromMilliseconds(150);

	/// <summary>Identifies the <see cref="IsProbesStale"/> property.</summary>
	public static readonly DependencyProperty IsProbesStaleProperty = DependencyProperty.Register(
		nameof(IsProbesStale), typeof(bool), typeof(PreviewPane), new PropertyMetadata(false, (d, _) => ((PreviewPane)d).UpdateSource()));

	/// <summary>Identifies the <see cref="ScratchFrame"/> property.</summary>
	public static readonly DependencyProperty ScratchFrameProperty = DependencyProperty.Register(
		nameof(ScratchFrame), typeof(ScratchResult), typeof(PreviewPane), new PropertyMetadata(null, (d, _) => ((PreviewPane)d).UpdateSource()));

	private static readonly DependencyPropertyKey GraphScratchKey = DependencyProperty.RegisterReadOnly(
		nameof(GraphScratch), typeof(ScratchResult), typeof(PreviewPane), new PropertyMetadata(null));

	/// <summary>Identifies the <see cref="GraphScratch"/> property.</summary>
	public static readonly DependencyProperty GraphScratchProperty = GraphScratchKey.DependencyProperty;

	private static readonly DependencyPropertyKey CanvasWidthKey = DependencyProperty.RegisterReadOnly(
		nameof(CanvasWidth), typeof(int), typeof(PreviewPane), new PropertyMetadata(1));

	/// <summary>Identifies the <see cref="CanvasWidth"/> property.</summary>
	public static readonly DependencyProperty CanvasWidthProperty = CanvasWidthKey.DependencyProperty;

	private static readonly DependencyPropertyKey CanvasHeightKey = DependencyProperty.RegisterReadOnly(
		nameof(CanvasHeight), typeof(int), typeof(PreviewPane), new PropertyMetadata(1));

	/// <summary>Identifies the <see cref="CanvasHeight"/> property.</summary>
	public static readonly DependencyProperty CanvasHeightProperty = CanvasHeightKey.DependencyProperty;

	/// <summary>The project's canvas width, bound privately; the canvas size follows the run while one exists.</summary>
	private static readonly DependencyProperty SceneWidthProperty = DependencyProperty.Register(
		"SceneWidth", typeof(int), typeof(PreviewPane), new PropertyMetadata(Scene.DefaultWidth, (d, _) => ((PreviewPane)d).UpdateCanvas()));

	/// <summary>The project's canvas height, bound privately.</summary>
	private static readonly DependencyProperty SceneHeightProperty = DependencyProperty.Register(
		"SceneHeight", typeof(int), typeof(PreviewPane), new PropertyMetadata(Scene.DefaultHeight, (d, _) => ((PreviewPane)d).UpdateCanvas()));

	/// <summary>Identifies the <see cref="FieldSettings"/> property.</summary>
	public static readonly DependencyProperty FieldSettingsProperty = DependencyProperty.Register(
		nameof(FieldSettings), typeof(DisplayLayer), typeof(PreviewPane), new PropertyMetadata(null, (d, e) => ((PreviewPane)d).OnLayerSettingsChanged(FrameLayer.Field, e)));

	/// <summary>Identifies the <see cref="IntensitySettings"/> property.</summary>
	public static readonly DependencyProperty IntensitySettingsProperty = DependencyProperty.Register(
		nameof(IntensitySettings), typeof(DisplayLayer), typeof(PreviewPane), new PropertyMetadata(null, (d, e) => ((PreviewPane)d).OnLayerSettingsChanged(FrameLayer.Intensity, e)));

	private readonly DispatcherTimer _timer;
	private readonly DispatcherTimer _busyDelay;
	private readonly Stopwatch _clock = new();
	private readonly FramePresenter _presenter;

	/// <summary>The picture of each layer once it has had a size, for <see cref="RefreshPictureSizes"/>.</summary>
	private readonly Dictionary<FrameLayer, Image> _pictures = [];

	/// <summary>The window whose DPI changes refresh the pictures' sizes, once subscribed.</summary>
	private Window? _dpiWindow;

	/// <summary>Fractional frame advance carried from one tick to the next, so slow speeds still move.</summary>
	private double _carry;

	/// <summary>Set while the pane itself moves <see cref="Position"/> to the newest frame, so that move does not end <see cref="IsLive"/>.</summary>
	private bool _following;

	/// <summary>The scratch frame currently wrapped as the presenter's source, to reuse the wrapper.</summary>
	private SnapshotFrameSource? _scratchSource;

	public PreviewPane()
	{
		InitializeComponent();
		_presenter = new FramePresenter(Dispatcher);
		// a 16-bit picture of a new size arrives with its first frame
		_presenter.BitmapsChanged += (_, _) => UpdateCanvas(_presenter.Source);
		// a window landing on a monitor of another scale (the process is DPI aware per monitor, 2026-11-02) changes the
		// pictures' size in device pixels while their layout size stays, so no SizeChanged comes: told again here
		Loaded += (_, _) =>
		{
			if (_dpiWindow == null && Window.GetWindow(this) is { } window)
			{
				_dpiWindow = window;
				window.DpiChanged += (_, _) => RefreshPictureSizes();
			}
		};

		// frames = ceil(steps / visualize every); re-evaluated whenever the project or its settings change. The
		// timeline is always the project's: a reduced preview (View > Preview frames) stores fewer frames, but
		// the run's Frames source stands a stored frame in for the ones left out (2026-10-15)
		SetBinding(FrameCountProperty, new MultiBinding
		{
			Converter = new FrameCountConverter(),
			Bindings =
			{
				new Binding($"{nameof(Project)}.{nameof(Model.Project.Simulation)}.{nameof(Simulation.Steps)}") { Source = this },
				new Binding($"{nameof(Project)}.{nameof(Model.Project.Simulation)}.{nameof(Simulation.VisualizeEvery)}") { Source = this },
			},
		});

		// in/out points in frames from the model's steps; "end" makes an ExportTo of 0 the last planned frame
		SetBinding(InPointProperty, RangeBinding(nameof(Display.ExportFrom), null));
		SetBinding(OutPointProperty, RangeBinding(nameof(Display.ExportTo), "end"));

		// the frame clock and the colouring of the two pictures follow the project's display settings
		SetBinding(FpsProperty, new Binding($"{nameof(Project)}.{nameof(Model.Project.Display)}.{nameof(Display.Video)}.{nameof(Video.Fps)}") { Source = this, Mode = BindingMode.OneWay });
		SetBinding(FieldSettingsProperty, new Binding($"{nameof(Project)}.{nameof(Model.Project.Display)}.{nameof(Display.Field)}") { Source = this, Mode = BindingMode.OneWay });
		SetBinding(IntensitySettingsProperty, new Binding($"{nameof(Project)}.{nameof(Model.Project.Display)}.{nameof(Display.Intensity)}") { Source = this, Mode = BindingMode.OneWay });
		SetBinding(SceneWidthProperty, new Binding($"{nameof(Project)}.{nameof(Model.Project.Scene)}.{nameof(Scene.Width)}") { Source = this, Mode = BindingMode.OneWay });
		SetBinding(SceneHeightProperty, new Binding($"{nameof(Project)}.{nameof(Model.Project.Scene)}.{nameof(Scene.Height)}") { Source = this, Mode = BindingMode.OneWay });
		UpdateCanvas();

		// render priority, on purpose (2026-10-16): a try at background priority, to put input first, gained nothing
		// measurable during a run and made the clock wait behind every mouse move while scrubbing
		_timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(1.0 / 60.0) };
		_timer.Tick += OnTick;
		_busyDelay = new DispatcherTimer(DispatcherPriority.Normal) { Interval = BusyOverlayDelay };
		_busyDelay.Tick += (_, _) =>
		{
			_busyDelay.Stop();
			IsBusyOverlayVisible = IsComputing;
		};
		Unloaded += (_, _) => IsPlaying = false;
	}

	private MultiBinding RangeBinding(string stepProperty, string? parameter) => new()
	{
		Converter = new StepToFrameConverter(),
		ConverterParameter = parameter,
		Mode = BindingMode.OneWay,
		Bindings =
		{
			new Binding($"{nameof(Project)}.{nameof(Model.Project.Display)}.{stepProperty}") { Source = this },
			new Binding($"{nameof(Project)}.{nameof(Model.Project.Simulation)}.{nameof(Simulation.VisualizeEvery)}") { Source = this },
			new Binding(nameof(PlannedLastFrame)) { Source = this },
		},
	};

	/// <summary>The project whose run is shown; null shows the placeholders with a single frame.</summary>
	public Project? Project
	{
		get => (Project?)GetValue(ProjectProperty);
		set => SetValue(ProjectProperty, value);
	}

	/// <summary>The run whose frames are shown (in progress or finished); null shows the placeholders.</summary>
	public SimulationRun? Run
	{
		get => (SimulationRun?)GetValue(RunProperty);
		set => SetValue(RunProperty, value);
	}

	/// <summary>Number of frames the project's settings would produce, at least 1. Bound to the steps and visualisation interval.</summary>
	public int FrameCount
	{
		get => (int)GetValue(FrameCountProperty);
		set => SetValue(FrameCountProperty, value);
	}

	/// <summary>Index of the last frame that can be shown: the newest frame of the run, or <see cref="PlannedLastFrame"/> without a run.</summary>
	public int LastFrame
	{
		get => (int)GetValue(LastFrameProperty);
		private set => SetValue(LastFrameKey, value);
	}

	/// <summary>Index of the last frame of the whole run (the run's own count while one exists, else the project's); the timeline's axis.</summary>
	public int PlannedLastFrame
	{
		get => (int)GetValue(PlannedLastFrameProperty);
		private set => SetValue(PlannedLastFrameKey, value);
	}

	/// <summary>Frames of the run computed so far; 0 without a run.</summary>
	public int Available
	{
		get => (int)GetValue(AvailableProperty);
		private set => SetValue(AvailableKey, value);
	}

	/// <summary>Frame shown, 0 to <see cref="LastFrame"/>. Moved by the scrubber and by playback.</summary>
	public int Position
	{
		get => (int)GetValue(PositionProperty);
		set => SetValue(PositionProperty, value);
	}

	/// <summary>Whether playback is running. Setting it starts or stops the frame clock.</summary>
	public bool IsPlaying
	{
		get => (bool)GetValue(IsPlayingProperty);
		set => SetValue(IsPlayingProperty, value);
	}

	/// <summary>Whether <see cref="Position"/> follows the newest frame of a run in progress. Only while a run is active; scrubbing, stepping, playing or the run's end turns it off.</summary>
	public bool IsLive
	{
		get => (bool)GetValue(IsLiveProperty);
		set => SetValue(IsLiveProperty, value);
	}

	/// <summary>Playback speed factor, <see cref="MinSpeed"/> to <see cref="MaxSpeed"/>; 1 is <see cref="Fps"/> frames per second.</summary>
	public double Speed
	{
		get => (double)GetValue(SpeedProperty);
		set => SetValue(SpeedProperty, value);
	}

	/// <summary>Frames per second at a <see cref="Speed"/> of 1: the project's video frame rate, so speed 1 plays as the export would.</summary>
	public int Fps
	{
		get => (int)GetValue(FpsProperty);
		set => SetValue(FpsProperty, value);
	}

	/// <summary>First frame of the export/loop range; 0 while no in point is set. Follows the model.</summary>
	public int InPoint
	{
		get => (int)GetValue(InPointProperty);
		private set => SetValue(InPointProperty, value);
	}

	/// <summary>Last frame of the export/loop range; <see cref="PlannedLastFrame"/> while no out point is set. Follows the model.</summary>
	public int OutPoint
	{
		get => (int)GetValue(OutPointProperty);
		private set => SetValue(OutPointProperty, value);
	}

	/// <summary>Whether playback wraps from the out point back to the in point instead of stopping.</summary>
	public bool IsLooping
	{
		get => (bool)GetValue(IsLoopingProperty);
		set => SetValue(IsLoopingProperty, value);
	}

	/// <summary>How the two pictures are split: <see cref="Orientation.Horizontal"/> side by side, <see cref="Orientation.Vertical"/> field above intensity.</summary>
	public Orientation Orientation
	{
		get => (Orientation)GetValue(OrientationProperty);
		set => SetValue(OrientationProperty, value);
	}

	/// <summary>
	/// The dithering of the 16-bit pictures (View > Preview dithering, 2026-10-23): 0 for none, else the bits per channel
	/// the colours carry into the Bayer matrix (10, 12, 14, 16). Preview only; the 8-bit pictures are not dithered.
	/// </summary>
	public int DitherBits
	{
		get => (int)GetValue(DitherBitsProperty);
		set => SetValue(DitherBitsProperty, value);
	}

	/// <summary>Picture of the field at <see cref="Position"/>; the presenter's bitmap, or a black frame without a run.</summary>
	public ImageSource? FieldImage
	{
		get => (ImageSource?)GetValue(FieldImageProperty);
		set => SetValue(FieldImageProperty, value);
	}

	/// <summary>
	/// The decoded scene image of an image scene, null for a blank canvas (2026-10-13). It is the picture
	/// shown where no frame exists, gives the canvas its size before any run, and is the source of the
	/// glowing-edges overlays (<see cref="FieldOverlay"/>, <see cref="IntensityOverlay"/>).
	/// </summary>
	public ScenePixels? ScenePixels
	{
		get => (ScenePixels?)GetValue(ScenePixelsProperty);
		set => SetValue(ScenePixelsProperty, value);
	}

	/// <summary>The outline layer over the field picture; null when off, at gain 0, or without a scene image.</summary>
	public ImageSource? FieldOverlay
	{
		get => (ImageSource?)GetValue(FieldOverlayProperty);
		private set => SetValue(FieldOverlayProperty, value);
	}

	/// <summary>The outline layer over the intensity picture; null when off, at gain 0, or without a scene image.</summary>
	public ImageSource? IntensityOverlay
	{
		get => (ImageSource?)GetValue(IntensityOverlayProperty);
		private set => SetValue(IntensityOverlayProperty, value);
	}

	/// <summary>Picture of the intensity at <see cref="Position"/>; the presenter's bitmap, or a black frame without a run (or without an intensity plane).</summary>
	public ImageSource? IntensityImage
	{
		get => (ImageSource?)GetValue(IntensityImageProperty);
		set => SetValue(IntensityImageProperty, value);
	}

	/// <summary>The source or probe selected in the project: highlighted on the pictures, set by grabbing or focusing a marker. Bound to the main window.</summary>
	public object? SelectedItem
	{
		get => GetValue(SelectedItemProperty);
		set => SetValue(SelectedItemProperty, value);
	}

	/// <summary>The shared selection of sources, parts and probes (the main window's), handed to the overlays, which highlight, extend and drag it.</summary>
	public ObservableCollection<object>? SelectedItems
	{
		get => (ObservableCollection<object>?)GetValue(SelectedItemsProperty);
		set => SetValue(SelectedItemsProperty, value);
	}

	/// <summary>Whether the project was edited since the run started, so the stored frames no longer show it; the available-frames shading goes grey.</summary>
	public bool IsStale
	{
		get => (bool)GetValue(IsStaleProperty);
		set => SetValue(IsStaleProperty, value);
	}

	/// <summary>
	/// A frame computed for the project as it is now (after an edit, without a run); shown instead of the
	/// stored frame while its frame index is <see cref="Position"/> and the store has nothing newer. Null for none.
	/// </summary>
	public ScratchResult? ScratchFrame
	{
		get => (ScratchResult?)GetValue(ScratchFrameProperty);
		set => SetValue(ScratchFrameProperty, value);
	}

	/// <summary>
	/// The scratch frame the probe graphs draw from, or null while they draw from the run: the scratch frame the
	/// pictures show, or, with only the probes stale (<see cref="IsProbesStale"/>: the pictures keep the stored
	/// frame, a probe never changes it), the scratch frame computed for the position (2026-10-14).
	/// </summary>
	public ScratchResult? GraphScratch
	{
		get => (ScratchResult?)GetValue(GraphScratchProperty);
		private set => SetValue(GraphScratchKey, value);
	}

	/// <summary>Whether a frame for <see cref="Position"/> is being computed (bound to the main window); after <see cref="BusyOverlayDelay"/> the pictures dim and a spinner turns until it arrives.</summary>
	public bool IsComputing
	{
		get => (bool)GetValue(IsComputingProperty);
		set => SetValue(IsComputingProperty, value);
	}

	/// <summary>Whether the dim and the spinner are shown over the pictures: <see cref="IsComputing"/> has lasted longer than <see cref="BusyOverlayDelay"/>.</summary>
	public bool IsBusyOverlayVisible
	{
		get => (bool)GetValue(IsBusyOverlayVisibleProperty);
		private set => SetValue(IsBusyOverlayVisibleKey, value);
	}

	/// <summary>Whether a probe was edited since the run started: its frames hold, its recordings do not. Bound to the main window.</summary>
	public bool IsProbesStale
	{
		get => (bool)GetValue(IsProbesStaleProperty);
		set => SetValue(IsProbesStaleProperty, value);
	}

	/// <summary>Width in pixels of the canvas the pictures show: the run's while one exists, else the project's blank canvas size.</summary>
	public int CanvasWidth
	{
		get => (int)GetValue(CanvasWidthProperty);
		private set => SetValue(CanvasWidthKey, value);
	}

	/// <summary>Height in pixels of the canvas the pictures show.</summary>
	public int CanvasHeight
	{
		get => (int)GetValue(CanvasHeightProperty);
		private set => SetValue(CanvasHeightKey, value);
	}

	/// <summary>The project's display settings of the field picture (scheme, brightness, black level, whether shown). Bound.</summary>
	public DisplayLayer? FieldSettings
	{
		get => (DisplayLayer?)GetValue(FieldSettingsProperty);
		set => SetValue(FieldSettingsProperty, value);
	}

	/// <summary>The project's display settings of the intensity picture. Bound.</summary>
	public DisplayLayer? IntensitySettings
	{
		get => (DisplayLayer?)GetValue(IntensitySettingsProperty);
		set => SetValue(IntensitySettingsProperty, value);
	}

	// ---------------------------------------------------------------- the run and its frames

	private static void OnRunChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var pane = (PreviewPane)d;
		if (e.OldValue is SimulationRun old)
		{
			old.PropertyChanged -= pane.OnRunPropertyChanged;
		}
		var run = (SimulationRun?)e.NewValue;
		if (run != null)
		{
			run.PropertyChanged += pane.OnRunPropertyChanged;
		}
		if (run is { IsAutomatic: true, IsFinished: false })
		{
			// a re-run after an edit: the position stays where the user is (the axis stays the planned run, so
			// nothing coerces it), the scratch frame stays on screen until the store has that frame, and the
			// run is not followed whatever the Live button said
			pane.UpdateFrames();
			pane.IsLive = false;
		}
		else if (run == null)
		{
			// the run was discarded (replaced, or the project closed): keep the position for whatever comes next
			pane.UpdateFrames();
			pane.IsLive = false;
		}
		else
		{
			// a run the user started: from the beginning, following it; playback (if any) stops
			pane.IsPlaying = false;
			pane.UpdateFrames();
			pane.Position = 0;
			// a run handed over before Start is still "Created": follow everything that has not ended
			pane.IsLive = !run.IsFinished;
		}
		pane.UpdateSource();
	}

	private void OnRunPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(SimulationRun.FrameCount):
				UpdateFrames();
				if (IsLive && !_presenter.IsBusy)
				{
					// follow the newest frame, but only when the previous picture is done: the next heartbeat (30 a
					// second) follows again, so the position trails the run by one picture at most
					_following = true;
					Position = NewestFrame;
					_following = false;
				}
				UpdateSource();
				break;
			case nameof(SimulationRun.State):
				UpdateFrames();
				if (IsLive && Run?.IsFinished == true)
				{
					// the end: follow to the last frame whatever the presenter is doing (the busy gate above skips a
					// follow while a picture is drawn and relies on the next heartbeat, and none comes after the end;
					// the live follow stopped at 90 of 99 on a short run, 2026-10-16)
					_following = true;
					Position = NewestFrame;
					_following = false;
				}
				UpdateSource();
				if (Run?.IsFinished == true)
				{
					IsLive = false; // nothing left to follow
				}
				CommandManager.InvalidateRequerySuggested();
				break;
		}
	}

	/// <summary>
	/// Decides what the pictures show. A picture changes only when a frame for <see cref="Position"/> itself is
	/// there: the scratch frame computed for it (preferred while the run is stale, missing, or not that far
	/// yet), else the run's stored frame. When neither exists (a run in progress that has not reached the
	/// position, after the previous run's frames were discarded) the last picture simply stays; the frames are
	/// never replayed from the start and never replaced by black or by some other frame. Also re-derives the
	/// canvas size (the frame's, the run's, or the project's blank canvas; a scene image's size is only known
	/// once it has run).
	/// </summary>
	private void UpdateSource()
	{
		SimulationRun? run = Run;
		ScratchResult? scratch = ScratchFrame;
		bool useScratch = scratch != null && scratch.Frame == Position
			&& (run == null || IsStale || run.FrameCount <= Position);
		if (useScratch)
		{
			if (_scratchSource == null || !ReferenceEquals(_scratchSource.Snapshot, scratch!.Snapshot))
			{
				_scratchSource = new SnapshotFrameSource(scratch!.Snapshot);
			}
			_presenter.Source = _scratchSource;
			GraphScratch = scratch;
			UpdateCanvas(_scratchSource);
			_shownFrame = 0;
			_presenter.Show(0);
		}
		else if (run != null && run.FrameCount > Position)
		{
			// the run's frames in timeline numbering (a reduced preview stands a stored frame in for the ones left out)
			_presenter.Source = run.Frames;
			// the stored frame is right even with the probes stale; the graphs then take the scratch recording for this position once it is there
			GraphScratch = IsProbesStale && scratch is { Snapshot: not null } && scratch.Frame == Position ? scratch : null;
			UpdateCanvas(run.Frames);
			_shownFrame = Position;
			_presenter.Show(Position);
		}
		else if (run == null && scratch == null && _presenter.Source != null)
		{
			// nothing at all any more (project closed or replaced: the window reset the scratch frame too). With a
			// scratch frame for another position pending, the last picture stays: clearing here made every scrub
			// without a run flash the empty picture before the new frame arrived (2026-10-13)
			_presenter.Source = null;
			GraphScratch = null;
			UpdateCanvas(null);
		}
		else
		{
			// nothing for this position yet: keep what is shown
			UpdateCanvas(_presenter.Source);
		}
	}

	private void UpdateCanvas(IFrameSource? source)
	{
		// the scene image's size wins over the project's numbers, which only apply to a blank canvas; a frame
		// source knows the canvas it was shrunk from (the handles and boxes live in canvas pixels, whatever the
		// picture's own size is)
		int width = Math.Max(1, source?.CanvasWidth ?? Run?.Spec.Width ?? ScenePixels?.Width ?? (int)GetValue(SceneWidthProperty));
		int height = Math.Max(1, source?.CanvasHeight ?? Run?.Spec.Height ?? ScenePixels?.Height ?? (int)GetValue(SceneHeightProperty));
		CanvasWidth = width;
		CanvasHeight = height;
		if (_blackFrame.PixelWidth != width || _blackFrame.PixelHeight != height)
		{
			_blackFrame = BlackFrame(width, height);
		}
		// without a frame, an image scene shows the scene image itself (the author's picking background), a canvas black
		BitmapSource empty = _sceneBitmap is { } scene && scene.PixelWidth == width && scene.PixelHeight == height ? scene : _blackFrame;
		FieldImage = _presenter.FieldBitmap ?? empty;
		IntensityImage = _presenter.IntensityBitmap ?? empty;
	}

	/// <summary>A new scene image (or none): rebuilds the picture, the edge picture and both overlays, and re-derives the canvas.</summary>
	private void OnScenePixelsChanged()
	{
		ScenePixels? pixels = ScenePixels;
		_sceneBitmap = pixels == null ? null : SceneBitmaps.Image(pixels);
		_edges = pixels == null ? null : GlowingEdges.Compute(pixels.Bgra, pixels.Width, pixels.Height, pixels.StrideBytes);
		UpdateOverlay(FrameLayer.Field);
		UpdateOverlay(FrameLayer.Intensity);
		UpdateCanvas();
	}

	/// <summary>Rebuilds one layer's outline from its settings: null when switched off, at gain 0, or without a scene image.</summary>
	private void UpdateOverlay(FrameLayer layer)
	{
		DisplayLayer? settings = layer == FrameLayer.Field ? FieldSettings : IntensitySettings;
		ImageSource? overlay = _edges != null && ScenePixels is { } pixels && settings is { Overlay: true }
			? SceneBitmaps.Overlay(_edges, pixels.Width, pixels.Height, settings.OverlayGain)
			: null;
		if (layer == FrameLayer.Field)
		{
			FieldOverlay = overlay;
		}
		else
		{
			IntensityOverlay = overlay;
		}
	}

	/// <summary>The canvas follows the project's size while nothing is shown.</summary>
	private void UpdateCanvas() => UpdateSource();

	/// <summary>Another project (new, opened, imported or closed): playback stops and the timeline goes back to frame 0 (the user's rule, 2026-10-15).</summary>
	private void OnProjectReplaced()
	{
		IsPlaying = false;
		IsLive = false;
		Position = 0;
	}

	/// <summary>A frame started computing: the overlay follows after the delay; the frame arrived: it goes at once.</summary>
	private void OnComputingChanged()
	{
		if (IsComputing)
		{
			_busyDelay.Stop();
			_busyDelay.Start();
		}
		else
		{
			_busyDelay.Stop();
			IsBusyOverlayVisible = false;
		}
	}

	/// <summary>A black picture of the given size, frozen; what the pane shows before any run.</summary>
	private static BitmapSource BlackFrame(int width, int height)
	{
		var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
		bitmap.Freeze();
		return bitmap;
	}

	/// <summary>
	/// Re-derives the axis, the reachable last frame and the available count from the run (or the project
	/// without one). For an automatic re-run the reachable end is always the planned run, filling or
	/// cancelled by the next edit, so the user's position is never pulled back; only a run the user started
	/// (and follows, or stopped) bounds the position by the frames that exist.
	/// </summary>
	private void UpdateFrames()
	{
		SimulationRun? run = Run;
		PlannedLastFrame = (run?.Spec.FrameCount ?? FrameCount) - 1;
		Available = run?.FrameCount ?? 0;
		LastFrame = run == null || run.IsAutomatic ? PlannedLastFrame : Math.Max(0, run.FrameCount - 1);
		CoerceValue(PositionProperty);
		CommandManager.InvalidateRequerySuggested();
	}

	/// <summary>The newest frame that exists: the run's last written frame, or the project's last frame without a run.</summary>
	private int NewestFrame => Run != null ? Math.Max(0, Run.FrameCount - 1) : PlannedLastFrame;

	private static void OnFrameCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PreviewPane)d).UpdateFrames();

	private static object CoercePosition(DependencyObject d, object value) =>
		Math.Clamp((int)value, 0, ((PreviewPane)d).LastFrame);

	private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var pane = (PreviewPane)d;
		if (!pane._following)
		{
			pane.IsLive = false;
		}
		pane.UpdateSource();
	}

	private static void OnIsLiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var pane = (PreviewPane)d;
		if ((bool)e.NewValue)
		{
			pane.IsPlaying = false;
			pane._following = true;
			pane.Position = pane.NewestFrame;
			pane._following = false;
		}
	}

	/// <summary>Re-wires the model's change notification for one layer's display settings and applies them.</summary>
	private void OnLayerSettingsChanged(FrameLayer layer, DependencyPropertyChangedEventArgs e)
	{
		PropertyChangedEventHandler handler = layer == FrameLayer.Field ? OnFieldSettingsPropertyChanged : OnIntensitySettingsPropertyChanged;
		if (e.OldValue is DisplayLayer old)
		{
			old.PropertyChanged -= handler;
		}
		if (e.NewValue is DisplayLayer settings)
		{
			settings.PropertyChanged += handler;
		}
		ApplyLayer(layer);
		UpdateOverlay(layer);
		UpdatePictureLayout();
	}

	private void OnFieldSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnLayerSettingChanged(FrameLayer.Field, e.PropertyName);

	private void OnIntensitySettingsPropertyChanged(object? sender, PropertyChangedEventArgs e) => OnLayerSettingChanged(FrameLayer.Intensity, e.PropertyName);

	/// <summary>An outline setting rebuilds that layer's overlay; anything else re-applies the palette.</summary>
	private void OnLayerSettingChanged(FrameLayer layer, string? property)
	{
		if (property is nameof(DisplayLayer.Overlay) or nameof(DisplayLayer.OverlayGain))
		{
			UpdateOverlay(layer);
		}
		else if (property != nameof(DisplayLayer.OverlayInExport))
		{
			ApplyLayer(layer);
		}
		if (property == nameof(DisplayLayer.Preview))
		{
			UpdatePictureLayout();
		}
	}

	/// <summary>
	/// A hidden picture gives its room to the other one (2026-10-13): with one picture off, the other
	/// spans the whole grid and the splitter is collapsed; with both on, the cells go back to the
	/// style's placement (the local values are cleared so the orientation triggers apply again) and the
	/// user's split survives, since the column and row sizes are never touched.
	/// </summary>
	private void UpdatePictureLayout()
	{
		bool field = FieldSettings?.Preview ?? true;
		bool intensity = IntensitySettings?.Preview ?? true;
		bool single = field != intensity;
		pictureSplitter.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
		foreach (HeaderedContentControl cell in new[] { fieldCell, intensityCell })
		{
			if (single)
			{
				cell.SetValue(Grid.ColumnProperty, 0);
				cell.SetValue(Grid.RowProperty, 0);
				cell.SetValue(Grid.ColumnSpanProperty, 3);
				cell.SetValue(Grid.RowSpanProperty, 3);
			}
			else
			{
				cell.ClearValue(Grid.ColumnProperty);
				cell.ClearValue(Grid.RowProperty);
				cell.ClearValue(Grid.ColumnSpanProperty);
				cell.ClearValue(Grid.RowSpanProperty);
			}
		}
	}

	/// <summary>Hands a layer's palette, brightness and visibility to the presenter, which redraws the frame shown.</summary>
	private void ApplyLayer(FrameLayer layer)
	{
		DisplayLayer? settings = layer == FrameLayer.Field ? FieldSettings : IntensitySettings;
		ColorLut lut = settings == null ? ColorLut.Grey : ColorSchemeCatalog.Instance.LutFor(Project, settings.ColorScheme, settings.BlackLevel);
		_presenter.SetLayer(layer, lut, (float)(settings?.Brightness ?? DisplayLayer.DefaultBrightness), settings?.Preview ?? true);
	}

	/// <summary>
	/// A picture's size on screen changed (the window, the split, one picture or two): the presenter renders a 16-bit
	/// frame at that size in device pixels, the picture's rounded layout size at the window's DPI and zoom, so its
	/// pixels land 1:1 on the screen's.
	/// </summary>
	private void OnPictureSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (sender is not Image { TemplatedParent: DependencyObject cell } image)
		{
			return;
		}
		FrameLayer layer = GetLayer(cell) == ProbeLayer.Field ? FrameLayer.Field : FrameLayer.Intensity;
		_pictures[layer] = image;
		SetPictureSize(layer, image);
	}

	/// <summary>The presenter told a picture's current size in device pixels.</summary>
	private void SetPictureSize(FrameLayer layer, Image image)
	{
		double scale = DevicePixels.PerDip(image);
		_presenter.SetTargetSize(layer, (int)Math.Round(image.RenderSize.Width * scale), (int)Math.Round(image.RenderSize.Height * scale));
	}

	/// <summary>Both pictures' sizes told again, after a DPI change.</summary>
	private void RefreshPictureSizes()
	{
		foreach ((FrameLayer layer, Image image) in _pictures)
		{
			SetPictureSize(layer, image);
		}
	}

	/// <summary>Rebuilds both palettes and redraws: a project colour scheme changed, or the project was replaced.</summary>
	public void RefreshColors()
	{
		ApplyLayer(FrameLayer.Field);
		ApplyLayer(FrameLayer.Intensity);
	}

	// ---------------------------------------------------------------- playback

	private static object CoerceSpeed(DependencyObject d, object value)
	{
		double speed = (double)value;
		return double.IsNaN(speed) ? 1.0 : Math.Clamp(speed, MinSpeed, MaxSpeed);
	}

	/// <summary>The last frame playback can reach: the out point, or the newest frame while the run has not got there yet.</summary>
	private int PlayEnd => Math.Min(OutPoint, NewestFrame);

	private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var pane = (PreviewPane)d;
		if ((bool)e.NewValue)
		{
			pane.IsLive = false;
			if (pane.Position >= pane.OutPoint)
			{
				pane.Position = pane.InPoint; // play the range again from its start
			}
			// playing means showing the simulation: the window starts a run when there is none or it is stale. Asked before
			// the clock starts (2026-11-03, the user's report): a run that does not fit asks in a modal window, whose message
			// loop had ticked the clock through the stale frames; a refused run turns playback off again, which lands here
			// with false before the command returns, so the clock stays off then
			SimulationCommands.EnsureRun.Execute(null, pane);
			if (!pane.IsPlaying)
			{
				return;
			}
			pane._carry = 0.0;
			pane._clock.Restart();
			pane._timer.Start();
		}
		else
		{
			pane._timer.Stop();
			pane._clock.Stop();
		}
		CommandManager.InvalidateRequerySuggested();
		pane.MoveFocusToCounterpart((bool)e.NewValue);
	}

	/// <summary>
	/// When the Play or Pause button has keyboard focus while its command just executed, moves
	/// focus to the other button, since CanExecute is about to disable this one and a disabled
	/// element cannot keep focus. Deferred behind the command re-query so the target is enabled.
	/// </summary>
	private void MoveFocusToCounterpart(bool playing)
	{
		if (playing && playButton.IsKeyboardFocused)
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, () => pauseButton.Focus());
		}
		else if (!playing && pauseButton.IsKeyboardFocused)
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, () => playButton.Focus());
		}
	}

	/// <summary>
	/// Advances <see cref="Position"/> by the frames elapsed since the previous tick. At the out
	/// point playback stops, or wraps to the in point when <see cref="IsLooping"/>.
	/// </summary>
	private void OnTick(object? sender, EventArgs e)
	{
		double seconds = _clock.Elapsed.TotalSeconds;
		_clock.Restart();
		_carry += seconds * Fps * Speed;
		int frames = (int)Math.Floor(_carry);
		if (frames == 0 || _presenter.IsBusy)
		{
			// nothing due, or the previous picture is still being rendered: the clock keeps counting and the next
			// tick moves by all the frames that fell due meanwhile (2026-10-16: moving the position sixty times a
			// second while eleven pictures a second could be shown redrew the overlays and the bar for nothing and
			// kept the UI thread in render passes, so clicks and keys waited)
			return;
		}
		_carry -= frames;
		int next = Position + frames;
		int end = PlayEnd;
		if (next < end)
		{
			Position = next;
		}
		else if (Run is { IsActive: true } && end < OutPoint)
		{
			// the run has not got this far yet: hold at its newest frame and wait, without accumulating a jump
			_following = true;
			Position = Math.Max(Position, end);
			_following = false;
			_carry = 0.0;
		}
		else if (IsLooping && end > InPoint)
		{
			Position = InPoint + (next - end) % (end - InPoint);
		}
		else
		{
			Position = end;
			IsPlaying = false;
		}
	}

	private void CanPlay(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = !IsPlaying && LastFrame > 0;

	private void OnPlay(object sender, ExecutedRoutedEventArgs e) => IsPlaying = true;

	private void CanPause(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = IsPlaying;

	private void OnPause(object sender, ExecutedRoutedEventArgs e) => IsPlaying = false;

	private void CanTogglePlayPause(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = LastFrame > 0;

	private void OnTogglePlayPause(object sender, ExecutedRoutedEventArgs e) => IsPlaying = !IsPlaying;

	// ---------------------------------------------------------------- transport commands

	/// <summary>The letter and punctuation gestures must not fire while the user types in a text box.</summary>
	private void CanTransport(object sender, CanExecuteRoutedEventArgs e) =>
		e.CanExecute = Project != null && e.OriginalSource is not TextBoxBase;

	private void OnStepBack(object sender, ExecutedRoutedEventArgs e) => Position -= 1;

	private void OnStepForward(object sender, ExecutedRoutedEventArgs e) => Position += 1;

	private void OnGoToStart(object sender, ExecutedRoutedEventArgs e) => Position = InPoint;

	private void OnGoToEnd(object sender, ExecutedRoutedEventArgs e) => Position = PlayEnd;

	private void CanEditRange(object sender, CanExecuteRoutedEventArgs e) =>
		e.CanExecute = Project != null && PlannedLastFrame > 0 && e.OriginalSource is not TextBoxBase;

	/// <summary>The frame a range command was given, or the frame shown.</summary>
	private int FrameOf(ExecutedRoutedEventArgs e) => e.Parameter is int frame ? Math.Clamp(frame, 0, PlannedLastFrame) : Position;

	/// <summary>
	/// Sets the in point, in steps, on the model. An in point past the out point clears the out
	/// point (0 = end of the run), so the range stays ordered.
	/// </summary>
	private void OnSetInPoint(object sender, ExecutedRoutedEventArgs e)
	{
		Display display = Project!.Display;
		int step = FrameOf(e) * Project.Simulation.VisualizeEvery;
		if (display.ExportTo != 0 && display.ExportTo <= step)
		{
			display.ExportTo = 0;
		}
		display.ExportFrom = step;
	}

	/// <summary>
	/// Sets the out point on the model; the last frame means "to the end" and is stored as 0. An
	/// out point before the in point clears the in point.
	/// </summary>
	private void OnSetOutPoint(object sender, ExecutedRoutedEventArgs e)
	{
		Display display = Project!.Display;
		int frame = FrameOf(e);
		int step = frame >= PlannedLastFrame ? 0 : frame * Project.Simulation.VisualizeEvery;
		if (step != 0 && display.ExportFrom >= step)
		{
			display.ExportFrom = 0;
		}
		display.ExportTo = step;
	}

	private void CanClearRange(object sender, CanExecuteRoutedEventArgs e) =>
		e.CanExecute = Project is { Display: { } display } && (display.ExportFrom != 0 || display.ExportTo != 0) && e.OriginalSource is not TextBoxBase;

	private void OnClearRange(object sender, ExecutedRoutedEventArgs e)
	{
		Project!.Display.ExportFrom = 0;
		Project.Display.ExportTo = 0;
	}

	private void OnToggleLoop(object sender, ExecutedRoutedEventArgs e) => IsLooping = !IsLooping;

	private void CanToggleLive(object sender, CanExecuteRoutedEventArgs e) =>
		e.CanExecute = Run?.IsActive == true && e.OriginalSource is not TextBoxBase;

	private void OnToggleLive(object sender, ExecutedRoutedEventArgs e) => IsLive = !IsLive;

	/// <summary>
	/// The pane's shortcut letters are its own even when their command cannot run right now (Live after the
	/// run ended, a range key with nothing to clear): left unhandled, WPF would treat the letter as a menu
	/// mnemonic, open the File menu and activate the window. Text boxes keep their keys.
	/// </summary>
	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (!e.Handled && e.OriginalSource is not TextBoxBase
			&& e.Key is Key.I or Key.O or Key.X or Key.L or Key.F or Key.OemComma or Key.OemPeriod or Key.Space
			&& Keyboard.Modifiers == ModifierKeys.None)
		{
			e.Handled = true;
		}
	}

	/// <summary>Space inside the speed text box is neither a digit nor the play/pause toggle: swallowed.</summary>
	private void OnSpeedBoxPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Space)
		{
			e.Handled = true;
		}
	}

	/// <summary>A click on the pictures gives the pane keyboard focus so that Space toggles playback.</summary>
	/// <summary>
	/// A click on the pictures gives the pane keyboard focus so that Space toggles playback, and a left click on
	/// empty space (the handles and graph boxes are thumbs, which handle their own press) deselects the current
	/// item (the user's rule, 2026-10-15), unless Shift or Ctrl is held: then a marquee that follows adds to the
	/// selection (2026-10-19).
	/// </summary>
	private void OnPicturesMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left && (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) == 0)
		{
			SelectedItem = null;
		}
		Focus();
	}

	/// <summary>Puts the splitter back in the middle: both pictures get an equal star share of the split dimension.</summary>
	private void ResetSplit()
	{
		var half = new GridLength(1, GridUnitType.Star);
		if (Orientation == Orientation.Horizontal)
		{
			pictures.ColumnDefinitions[0].Width = half;
			pictures.ColumnDefinitions[2].Width = half;
		}
		else
		{
			pictures.RowDefinitions[0].Height = half;
			pictures.RowDefinitions[2].Height = half;
		}
	}

	private void OnSplitterMouseDoubleClick(object sender, MouseButtonEventArgs e) => ResetSplit();

	/// <summary>Enter on the focused splitter is the keyboard's double-click; the arrow keys move it (built in).</summary>
	private void OnSplitterKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			ResetSplit();
			e.Handled = true;
		}
	}

	/// <summary>
	/// The arrow keys inside the split group move to the neighbouring radio and check it, as a
	/// Win32 radio group does (WPF's own directional navigation only moves focus, and skips
	/// elements that are not tab stops). Only the checked radio is a tab stop, so Tab lands on
	/// the current choice. SetCurrentValue keeps the IsChecked binding alive.
	/// </summary>
	private void OnSplitGroupKeyDown(object sender, KeyEventArgs e)
	{
		int step = e.Key switch { Key.Right or Key.Down => 1, Key.Left or Key.Up => -1, _ => 0 };
		if (step == 0 || e.OriginalSource is not RadioButton current)
		{
			return;
		}
		var radios = splitGroup.Children.OfType<RadioButton>().ToList();
		RadioButton next = radios[(radios.IndexOf(current) + step + radios.Count) % radios.Count];
		next.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
		next.Focus();
		e.Handled = true;
	}

	/// <summary>The context menu's "Brightness": the keyboard focus goes to the brightness box in that picture's header.</summary>
	private void OnEditBrightness(object sender, ExecutedRoutedEventArgs e)
	{
		HeaderedContentControl cell = e.Parameter is ProbeLayer.Intensity ? intensityCell : fieldCell;
		if (cell.Template?.FindName("brightnessBox", cell) is UIElement box)
		{
			box.Focus();
			Keyboard.Focus(box);
		}
	}
}
