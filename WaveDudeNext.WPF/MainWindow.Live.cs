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
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WaveDudeNext.Model;
using WaveDudeNext.Parts;
using WaveDudeNext.Rendering;
using WaveDudeNext.Runs;
using WaveDudeNext.WPF.Rendering;

namespace WaveDudeNext.WPF;

/// <summary>
/// Keeps the preview current after edits: a <see cref="ProjectWatcher"/> notices every change, a
/// simulation-affecting one marks the run stale (<see cref="IsRunStale"/>), asks the <see cref="ScratchRunner"/>
/// for the frame the preview shows (debounced, newest wins) and, when <see cref="Settings.AutoRerun"/> is on,
/// starts a stored re-run once the user has been idle for a moment; an edit during an automatic run stops
/// that run at once, since its snapshot is obsolete. Scrubbing asks for scratch frames whenever the store
/// cannot show the position: no run, a stale run, or a run that has not got there yet. The scratch runner
/// has its own engine, and any run in progress is paused while a scratch frame computes, so the frame the
/// user looks at always comes first (the user's rule, 2026-10-14).
/// </summary>
public partial class MainWindow
{
	/// <summary>How long after the last edit or scrub a scratch frame is requested.</summary>
	private static readonly TimeSpan ScratchDelay = TimeSpan.FromMilliseconds(120);

	/// <summary>How long the user must be idle after an edit before the stored run is redone.</summary>
	private static readonly TimeSpan RerunDelay = TimeSpan.FromMilliseconds(1500);

	/// <summary>Identifies the <see cref="IsRunStale"/> property.</summary>
	public static readonly DependencyProperty IsRunStaleProperty = DependencyProperty.Register(
		nameof(IsRunStale), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

	/// <summary>Identifies the <see cref="IsComputingFrame"/> property.</summary>
	public static readonly DependencyProperty IsComputingFrameProperty = DependencyProperty.Register(
		nameof(IsComputingFrame), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

	/// <summary>Identifies the <see cref="IsProbesStale"/> property.</summary>
	public static readonly DependencyProperty IsProbesStaleProperty = DependencyProperty.Register(
		nameof(IsProbesStale), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

	/// <summary>Identifies the <see cref="ScratchFrame"/> property.</summary>
	public static readonly DependencyProperty ScratchFrameProperty = DependencyProperty.Register(
		nameof(ScratchFrame), typeof(ScratchResult), typeof(MainWindow), new PropertyMetadata(null));

	/// <summary>Identifies the <see cref="PreviewPosition"/> property.</summary>
	public static readonly DependencyProperty PreviewPositionProperty = DependencyProperty.Register(
		nameof(PreviewPosition), typeof(int), typeof(MainWindow), new PropertyMetadata(0, (d, _) => ((MainWindow)d).OnPreviewPositionChanged()));

	/// <summary>Identifies the <see cref="IsPreviewPlaying"/> property.</summary>
	public static readonly DependencyProperty IsPreviewPlayingProperty = DependencyProperty.Register(
		nameof(IsPreviewPlaying), typeof(bool), typeof(MainWindow), new PropertyMetadata(false, (d, _) => ((MainWindow)d).OnPreviewPlayingChanged()));

	private ProjectWatcher? _watcher;
	/// <summary>Keeps the sources on a logic gate's inputs there when the gate is edited.</summary>
	private GatePortFollower? _follower;
	private ScratchRunner? _scratch;
	private SimulationRun? _pausedForScratch;
	private DispatcherTimer? _scratchTimer;
	private DispatcherTimer? _rerunTimer;
	private (RunSpec Spec, int Frame)? _scratchRequested;

	/// <summary>The newest simulation or probe edit, named with its value on the next scratch request's log line (2026-10-27); null once named.</summary>
	private ProjectChange? _lastChange;
	private (string Path, DateTime Written, ScenePixels Pixels)? _imageCache;
	private (byte[] Data, ScenePixels Pixels)? _embeddedCache;
	private (ScenePixels? Source, int PartsVersion, int Width, int Height, double Background, ScenePixels? Composed)? _composedCache;
	/// <summary>Bumped on every optical part change, so <see cref="LoadSceneImage"/> paints again only then.</summary>
	private int _partsVersion;

	/// <summary>Whether the project was edited in a way that changes the simulation since the current run started.</summary>
	public bool IsRunStale
	{
		get => (bool)GetValue(IsRunStaleProperty);
		set => SetValue(IsRunStaleProperty, value);
	}

	/// <summary>
	/// Whether a probe was edited (measurement, position, list) since the current run started: its frames are
	/// still right, its recordings are not. The graphs then draw from the scratch frame's recording, and Play
	/// or F5 starts a run that refreshes them; no run is started by the edit itself (the user's rule,
	/// 2026-10-14: a probe never changes the picture, and a drag must not launch a simulation).
	/// </summary>
	public bool IsProbesStale
	{
		get => (bool)GetValue(IsProbesStaleProperty);
		set => SetValue(IsProbesStaleProperty, value);
	}

	/// <summary>
	/// Whether a scratch frame for the preview's position is being computed: from the request until its result
	/// (or a newer request's). The preview dims its pictures and shows a spinner meanwhile, so the wait reads as
	/// work in progress rather than as a preview that does not update (the author's impression; the user's
	/// rule, 2026-10-14).
	/// </summary>
	public bool IsComputingFrame
	{
		get => (bool)GetValue(IsComputingFrameProperty);
		private set => SetValue(IsComputingFrameProperty, value);
	}

	/// <summary>The newest scratch frame (or skip notice) for the preview's position; null when none applies.</summary>
	public ScratchResult? ScratchFrame
	{
		get => (ScratchResult?)GetValue(ScratchFrameProperty);
		set => SetValue(ScratchFrameProperty, value);
	}

	/// <summary>The preview's frame index, bound from the pane so scratch requests follow it.</summary>
	public int PreviewPosition
	{
		get => (int)GetValue(PreviewPositionProperty);
		set => SetValue(PreviewPositionProperty, value);
	}

	/// <summary>Whether the preview is playing, bound from the pane: no scratch frame is requested while it is (see <see cref="ScheduleScratch"/>).</summary>
	public bool IsPreviewPlaying
	{
		get => (bool)GetValue(IsPreviewPlayingProperty);
		set => SetValue(IsPreviewPlayingProperty, value);
	}

	/// <summary>A new project: watch it instead of the old one; nothing is stale and no scratch frame applies.</summary>
	private void OnProjectReplaced(Project? project)
	{
		SelectedItems.Clear();
		_watcher?.Dispose();
		_watcher = null;
		_follower?.Dispose();
		_follower = null;
		_imageCache = null;
		_embeddedCache = null;
		_composedCache = null;
		// a new set of parts: a composition of the old project still on its worker must not land under the new one's key
		_partsVersion++;
		ForgetExportPaths();
		// the new project's own schemes and scene image; the pane does not exist yet when the constructor sets the first project
		Dispatcher.InvokeAsync(() =>
		{
			previewPane?.RefreshColors();
			UpdateScenePixels();
		}, DispatcherPriority.Background);
		IsRunStale = false;
		IsProbesStale = false;
		IsDirty = false;
		ScratchFrame = null;
		StartHistory(project);
		if (project != null)
		{
			_watcher = new ProjectWatcher(project);
			_watcher.Changed += OnProjectChanged;
			_follower = new GatePortFollower(project);
			// warm up: the first scratch frame builds the engine (an OpenCL compile), better now than on the first click
			ScheduleScratch();
		}
	}

	private void OnProjectChanged(object? sender, ProjectChange change)
	{
		if (Logger.IsTraceEnabled)
		{
			// every change with its value before and after (2026-10-27); Trace, so a Release build pays nothing for it
			Logger.Trace("Changed ({Item}.{Property}){Values}", change.Item, change.Property ?? "list", change.Values == null ? "" : ": " + change.OldValueText + " → " + change.NewValueText);
		}
		IsDirty = true;
		ScheduleRecord();
		bool sceneReplaced = change.Item is Project && change.Property == nameof(Project.Scene);
		if (change.Item is Scene && change.Property is nameof(Scene.ImagePath) or nameof(Scene.ImageData) || sceneReplaced)
		{
			// a new image file to watch (Browse, a reload, a path made relative, a scene written back by Undo or Redo)
			ArmSceneImageWatch();
		}
		if (change.Item is ColorScheme or ObservableCollection<ColorScheme>)
		{
			// a project scheme edited, added or removed: recolour the frame shown
			previewPane.RefreshColors();
			return;
		}
		bool unrecorded = change.AffectsProbes && HasUnrecordedProbe();
		if (change.AffectsProbes && !unrecorded)
		{
			// the frames stay, the recordings are stale: the scratch frame brings the new recording for the position
			if (Run != null && !IsProbesStale)
			{
				Logger.Debug("Probes edited ({Item}.{Property}): the run's recordings are stale", change.Item, change.Property);
				IsProbesStale = true;
			}
			_lastChange = change;
			ScheduleScratch();
			return;
		}
		if (!change.AffectsSimulation && !unrecorded && !IsBrightnessBaked(change))
		{
			return;
		}
		_lastChange = change;
		if (Run != null && !IsRunStale)
		{
			if (unrecorded)
			{
				Logger.Debug("Probes edited ({Item}.{Property}): an active probe has no recording in the run, so the run is stale", change.Item, change.Property ?? "list");
			}
			else
			{
				Logger.Debug("Edited ({Item}.{Property}): the run is stale", change.Item, change.Property ?? "list");
			}
		}
		if (IsPartChange(change))
		{
			// the parts are painted into the scene pixels: paint again, so the outline follows the edit at once
			_partsVersion++;
		}
		if (change.Item is Scene || sceneReplaced || IsPartChange(change))
		{
			UpdateScenePixels();
		}
		if (Run != null)
		{
			IsRunStale = true;
		}
		if (previewPane.IsPlaying)
		{
			// the whole run is stale: playing its frames on, or computing more of them, serves nothing (the user's
			// rule, 2026-10-16); stopped first, so the scratch frame below is for the position playback halted at
			Logger.Debug("Playback stopped: the run is stale");
			previewPane.IsPlaying = false;
		}
		if (Run is { IsActive: true })
		{
			// its snapshot is already obsolete: stop it now, the quiet period starts a fresh one. Any run, since
			// 2026-10-16 (the user's rule: an edit makes the whole run moot, whoever started it); before, a run the
			// user had started went on and was only marked stale
			Logger.Debug("The {Kind} is stopped: its snapshot is obsolete", Run.IsAutomatic ? "automatic run" : "run");
			Run.Stop();
		}
		ScheduleScratch();
		ScheduleRerun();
	}

	/// <summary>Whether the change is to an optical part, one of its points or fibre nodes, a point or node list, or the parts list.</summary>
	private static bool IsPartChange(ProjectChange change) =>
		change.Item is OpticalPart or PartPoint or FiberNode or ObservableCollection<OpticalPart> or ObservableCollection<PartPoint> or ObservableCollection<FiberNode>
			|| change.Item is Project && change.Property == nameof(Project.Parts);

	/// <summary>
	/// Whether an active probe has no recording in the current run: one just switched on, or one just added. A
	/// scratch frame fills its graph for one position only, and a scrub or playback showed it empty until a run
	/// recorded it, so such a probe edit counts as a simulation edit (the user's choice, 2026-10-18). A move or a
	/// measurement change of a recorded probe only stales the recordings, as before. UI thread.
	/// </summary>
	private bool HasUnrecordedProbe() =>
		Run is { } run && Project is { } project
			&& project.Probes.Any(probe => probe.Active && !run.Spec.Probes.Any(spec => ReferenceEquals(spec.Origin, probe)));

	/// <summary>
	/// Whether a display edit changes the stored frames after all: a layer's brightness while the preview's
	/// samples are 8-bit palette indices (View > Preview samples), which bake the brightness in at capture time.
	/// Such an edit then counts as a simulation edit: stale run, scratch frame, automatic re-run.
	/// </summary>
	private static bool IsBrightnessBaked(ProjectChange change) =>
		change.Item is DisplayLayer && change.Property == nameof(DisplayLayer.Brightness) && Settings.Default.PreviewSampleBytes == 1;

	/// <summary>
	/// View > Preview resolution / frames / samples changed: the run and the scratch frame on screen were made
	/// with the old shape, so both are redone as after an edit (the run marked stale, an automatic one stopped,
	/// the position's frame requested, the re-run scheduled).
	/// </summary>
	private void OnPreviewQualityChanged()
	{
		if (Run != null)
		{
			IsRunStale = true;
			if (Run is { IsAutomatic: true, IsActive: true })
			{
				Logger.Debug("The automatic run is stopped: the preview quality changed");
				Run.Stop();
			}
		}
		ScheduleScratch();
		ScheduleRerun();
	}

	/// <summary>Whether the store cannot show the preview's position, or its recordings are stale: no run, a stale one, stale probes, or a run that has not reached it.</summary>
	private bool NeedsScratch => Run == null || IsRunStale || IsProbesStale || Run.FrameCount <= PreviewPosition;

	private void OnPreviewPositionChanged()
	{
		if (NeedsScratch)
		{
			ScheduleScratch();
		}
	}

	/// <summary>Playback stopped at a position the store cannot show: now the frame for it is asked for.</summary>
	private void OnPreviewPlayingChanged()
	{
		if (!IsPreviewPlaying && NeedsScratch)
		{
			ScheduleScratch();
		}
	}

	/// <summary>
	/// After <see cref="ScratchDelay"/> of quiet, asks for the frame at the preview's position. Not while the preview
	/// plays (2026-10-16, after an edit during playback kept playing the old frames until playback ended): a scratch
	/// frame could never serve then, the position has moved on before it arrives, and every playback step restarted
	/// this timer, so it never fired and the automatic re-run, which waits behind a pending scratch frame, never
	/// started. A simulation edit stops playback at once (<see cref="OnProjectChanged"/>); what plays on is a run
	/// that has not reached the position, and the frame for the final position is asked for when playback stops
	/// (<see cref="OnPreviewPlayingChanged"/>).
	/// </summary>
	private void ScheduleScratch()
	{
		if (IsPreviewPlaying)
		{
			return;
		}
		_scratchTimer ??= MakeTimer(ScratchDelay, RequestScratch);
		_scratchTimer.Stop();
		_scratchTimer.Start();
	}

	private void RequestScratch()
	{
		_scratchTimer!.Stop();
		if (!NeedsScratch || Project.Simulation.Steps < 1)
		{
			return;
		}
		RunSpec? spec = TrySnapshotProject();
		if (spec == null)
		{
			return;
		}
		if (_scratch == null)
		{
			_scratch = new ScratchRunner(SynchronizationContext.Current);
			_scratch.Completed += OnScratchCompleted;
		}
		// the user's frame comes first: a run in progress, automatic or started by the user, would otherwise take
		// every core (CPU) or the card's time away from it; it continues once the frame is there (any run since
		// 2026-10-14; a run the user paused stays paused)
		if (Run is { IsActive: true } run && !run.IsPauseRequested)
		{
			run.Pause();
			_pausedForScratch = run;
		}
		int frame = Math.Clamp(PreviewPosition, 0, Math.Max(0, spec.FrameCount - 1));
		// the newest edit with its value (2026-10-27): after the debounce, so a typed number shows as typed, not keystroke by keystroke
		string edit = _lastChange == null ? "" : ": " + _lastChange.Item + "." + (_lastChange.Property ?? "list") + (_lastChange.Values == null ? "" : " = " + _lastChange.NewValueText);
		_lastChange = null;
		Logger.Debug("Preview frame {Frame} requested from scratch ({Reason}{Edit})", frame,
			Run == null ? "no run" : IsRunStale ? "the run is stale" : IsProbesStale ? "the probes changed" : "the run has not got there yet", edit);
		_scratchRequested = (spec, frame);
		_scratch.Request(spec, frame);
		SetScratchBusy(frame);
		IsComputingFrame = true;
	}

	private void OnScratchCompleted(object? sender, ScratchResult result)
	{
		// the request this result answers is the newest one: the indicator goes off (a newer request keeps it on)
		if (_scratchRequested is { } requested && ReferenceEquals(requested.Spec, result.Spec) && requested.Frame == result.Frame)
		{
			_scratchRequested = null;
			SetScratchBusy(-1);
			IsComputingFrame = false;
		}
		if (_pausedForScratch is { } paused)
		{
			_pausedForScratch = null;
			if (ReferenceEquals(paused, Run) && paused.IsActive)
			{
				paused.Resume();
			}
		}
		ScratchFrame = result;
	}

	/// <summary>After <see cref="RerunDelay"/> of quiet, redoes the stored run when the setting is on and the run is stale.</summary>
	private void ScheduleRerun()
	{
		if (!Settings.Default.AutoRerun)
		{
			return;
		}
		_rerunTimer ??= MakeTimer(RerunDelay, AutoRerun);
		_rerunTimer.Stop();
		_rerunTimer.Start();
	}

	private void AutoRerun()
	{
		_rerunTimer!.Stop();
		if (!Settings.Default.AutoRerun || !IsRunStale || Run?.IsActive == true)
		{
			Logger.Debug("No automatic re-run: {Reason}", !Settings.Default.AutoRerun ? "switched off" : !IsRunStale ? "the run is not stale" : "a run is active");
			return;
		}
		if (_scratch?.IsBusy == true || _scratchTimer?.IsEnabled == true || IsExporting)
		{
			// the frame the user is looking at comes first, requested or computing; the run would take the cores away
			// from it. An export reads the current run's store, so it keeps its run until it is done.
			Logger.Debug("Automatic re-run postponed: {Reason}", IsExporting ? "an export is in progress" : "a preview frame is being computed");
			ScheduleRerun();
			return;
		}
		try
		{
			StartRun(automatic: true);
		}
		catch (Exception ex) when (ex is RunSpecException or IOException or UnauthorizedAccessException or NotSupportedException)
		{
			// no message box for something the user did not ask for; the status bar shows the error
			Logger.Error(ex, "The automatic re-run could not start: {Message}", ex.Message);
		}
	}

	/// <summary>Called when a run ends: edits made meanwhile are still pending, so look again.</summary>
	private void OnRunFinished()
	{
		if (IsRunStale)
		{
			ScheduleScratch();
			ScheduleRerun();
		}
		else if (IsProbesStale)
		{
			ScheduleScratch();
		}
	}

	/// <summary>The project as a run spec with the scene image decoded (cached by path and write time), or null when it cannot run (logged at Debug).</summary>
	private RunSpec? TrySnapshotProject()
	{
		try
		{
			return RunSpec.Create(Project, LoadSceneImage(), Settings.Default.UseGpu, quality: PreviewQualitySetting);
		}
		catch (Exception ex) when (ex is RunSpecException or IOException or UnauthorizedAccessException or NotSupportedException)
		{
			Logger.Debug("No preview: {Message}", ex.Message);
			return null;
		}
	}

	/// <summary>
	/// Hands the preview the scene pixels (null for a canvas without parts, or when the image cannot be read: that is
	/// logged once the run reports it). Coalesced at background priority and composed on a worker (2026-11-03, the user's
	/// choice): a drag of a part fires a change per mouse move, and composing the scene took 115–270 ms of the UI thread
	/// for Example 16's four rectangles, so the handle jumped; now one composition is in flight at a time, the newest
	/// request follows it, and the UI thread only takes the finished pixels (20–30 ms for the pictures). The overlay
	/// draws the selected part's outline itself, so what the user drags stays live meanwhile.
	/// </summary>
	private void UpdateScenePixels()
	{
		if (_scenePixelsPending)
		{
			return;
		}
		_scenePixelsPending = true;
		Dispatcher.InvokeAsync(() =>
		{
			_scenePixelsPending = false;
			ComposeScene();
		}, DispatcherPriority.Background);
	}

	private bool _scenePixelsPending;

	/// <summary>The composition on a worker with the key its result is cached under, or null when none is in flight. UI thread only.</summary>
	private (SceneKey Key, Task<ScenePixels?> Task)? _composition;

	/// <summary>Set by a request that arrives while a composition is in flight: another one follows it.</summary>
	private bool _composeAgain;

	/// <summary>
	/// Composes the scene on a worker from a snapshot of its inputs (the parts cloned: <see cref="OpticalPart.Clone"/>
	/// copies the point and node lists too, so the worker shares nothing with the model) and hands the preview the result
	/// on the UI thread, again while requests arrived meanwhile. A failure of the painter surfaces here like any other.
	/// </summary>
	private async void ComposeScene()
	{
		if (previewPane == null)
		{
			return;
		}
		if (_composition != null)
		{
			_composeAgain = true;
			return;
		}
		do
		{
			_composeAgain = false;
			ScenePixels? source;
			try
			{
				source = DecodeSceneImage();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or RunSpecException)
			{
				Logger.Debug("Scene image not shown: {Message}", ex.Message);
				previewPane.ScenePixels = null;
				return;
			}
			Scene scene = Project.Scene;
			var key = new SceneKey(source, _partsVersion, scene.Width, scene.Height, scene.RefractiveIndex);
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			if (!TryCachedScene(key, out ScenePixels? composed))
			{
				List<OpticalPart> parts = Project.Parts.Select(part => part.Clone()).ToList();
				Task<ScenePixels?> task = Task.Run(() => PartPainter.Paint(key.Source, key.Width, key.Height, key.Background, parts));
				_composition = (key, task);
				try
				{
					composed = await task;
				}
				finally
				{
					_composition = null;
				}
				_composedCache = (key.Source, key.PartsVersion, key.Width, key.Height, key.Background, composed);
			}
			long painted = System.Diagnostics.Stopwatch.GetTimestamp();
			previewPane.ScenePixels = composed;
			Logger.Debug("Scene pixels updated: composed on a worker in {Paint:0.0} ms, pictures in {Pictures:0.0} ms", System.Diagnostics.Stopwatch.GetElapsedTime(started, painted).TotalMilliseconds, System.Diagnostics.Stopwatch.GetElapsedTime(painted).TotalMilliseconds);
		}
		while (_composeAgain);
	}

	/// <summary>What a composed scene depends on: the decoded image (by reference), the parts' version, the canvas and the background index.</summary>
	private readonly record struct SceneKey(ScenePixels? Source, int PartsVersion, int Width, int Height, double Background);

	/// <summary>The composed scene cached for these inputs, when it is the one.</summary>
	private bool TryCachedScene(SceneKey key, out ScenePixels? composed)
	{
		if (_composedCache is { } cached && ReferenceEquals(cached.Source, key.Source) && cached.PartsVersion == key.PartsVersion
			&& cached.Width == key.Width && cached.Height == key.Height && cached.Background == key.Background)
		{
			composed = cached.Composed;
			return true;
		}
		composed = null;
		return false;
	}

	/// <summary>
	/// The scene as the engines, the outline and the export see it: the decoded image (decoded once per file version)
	/// with the enabled optical parts painted in, or the parts on a blank canvas; null for a canvas without parts.
	/// Composed once per (image, parts version, canvas size, background index): an unchanged scene returns the same
	/// reference, which <see cref="RunSpec.SameSceneAs"/> compares, so the scratch runner keeps its engine. A worker
	/// painting these very inputs (<see cref="ComposeScene"/>) is waited for rather than painting them again.
	/// </summary>
	private ScenePixels? LoadSceneImage()
	{
		ScenePixels? source = DecodeSceneImage();
		Scene scene = Project.Scene;
		var key = new SceneKey(source, _partsVersion, scene.Width, scene.Height, scene.RefractiveIndex);
		if (TryCachedScene(key, out ScenePixels? composed))
		{
			return composed;
		}
		composed = _composition is { } composing && composing.Key == key
			? composing.Task.GetAwaiter().GetResult()
			: PartPainter.Paint(source, scene.Width, scene.Height, scene.RefractiveIndex, Project.Parts);
		_composedCache = (source, _partsVersion, scene.Width, scene.Height, scene.RefractiveIndex, composed);
		return composed;
	}

	/// <summary>The decoded scene image for an image scene, decoded once per file version; null for a blank canvas.</summary>
	private ScenePixels? DecodeSceneImage()
	{
		if (Project.Scene.Mode != SceneMode.Image)
		{
			return null;
		}
		if (Project.Scene.ImageData is { } data)
		{
			// embedded: decoded once per byte array (a new pick is a new array)
			if (_embeddedCache is { } embedded && ReferenceEquals(embedded.Data, data))
			{
				return embedded.Pixels;
			}
			ScenePixels decoded = SceneImageLoader.Load(data);
			_embeddedCache = (data, decoded);
			return decoded;
		}
		string path = ResolveScenePath();
		DateTime written = File.GetLastWriteTimeUtc(path);
		if (_imageCache is { } cached && cached.Path == path && cached.Written == written)
		{
			return cached.Pixels;
		}
		ScenePixels pixels = SceneImageLoader.Load(path);
		_imageCache = (path, written, pixels);
		return pixels;
	}

	private DispatcherTimer MakeTimer(TimeSpan interval, Action tick)
	{
		var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
		timer.Tick += (_, _) => tick();
		return timer;
	}

	private void ShutdownScratch()
	{
		DisarmSceneImageWatch();
		_scratchRequested = null;
		_scratchTimer?.Stop();
		_rerunTimer?.Stop();
		_scratch?.Dispose();
		_scratch = null;
		_watcher?.Dispose();
		_watcher = null;
		_follower?.Dispose();
		_follower = null;
	}
}
